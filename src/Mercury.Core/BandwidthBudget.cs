namespace Mercury;

public sealed class BandwidthBudget
{
    private readonly object _lock = new();
    private double _globalTokens;
    private long _globalLast;
    private double _idleTokens;
    private long _idleLast;
    private readonly Dictionary<string, Bucket> _jobs = new(StringComparer.Ordinal);
    private long _windowBytes;
    private long _windowStart;

    public double? GlobalMinBytesPerSecond { get; set; }
    public double? GlobalMaxBytesPerSecond { get; set; }
    public double? IdleThrottleBytesPerSecond { get; set; }
    public IMachineLoad MachineLoad { get; set; } = new MachineLoadSampler();

    public void Apply(BandwidthSettings settings)
    {
        GlobalMinBytesPerSecond = settings.GlobalMinBytesPerSecond;
        GlobalMaxBytesPerSecond = settings.GlobalMaxBytesPerSecond;
        IdleThrottleBytesPerSecond = settings.IdleThrottleBytesPerSecond;
        if (MachineLoad is MachineLoadSampler sampler)
        {
            sampler.BusyThresholdPercent = settings.IdleCpuPercentThreshold > 0
                ? settings.IdleCpuPercentThreshold
                : MachineLoadSampler.DefaultBusyPercent;
        }
    }

    public async Task ConsumeAsync(string jobId, double? jobMaxBytesPerSecond, int bytes, CancellationToken cancellationToken)
    {
        if (bytes <= 0)
        {
            return;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan wait;
            lock (_lock)
            {
                wait = TryConsume(jobId, jobMaxBytesPerSecond, bytes);
            }

            if (wait <= TimeSpan.Zero)
            {
                return;
            }

            var delay = wait > TimeSpan.FromMilliseconds(5) ? wait : TimeSpan.FromMilliseconds(5);
            if (delay > TimeSpan.FromSeconds(2))
            {
                delay = TimeSpan.FromSeconds(2);
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    public TimeSpan TryConsume(string jobId, double? jobMaxBytesPerSecond, int bytes)
    {
        var now = StopwatchTimestamp();
        RefillWindow(now);
        RefreshLoad();

        var skipGlobal = ShouldSkipGlobalThrottle(now);

        if (!skipGlobal && GlobalMaxBytesPerSecond is > 0)
        {
            var globalWait = ConsumeBucket(ref _globalTokens, ref _globalLast, GlobalMaxBytesPerSecond.Value, bytes, now);
            if (globalWait > TimeSpan.Zero)
            {
                return globalWait;
            }
        }
        else if (GlobalMaxBytesPerSecond is > 0)
        {
            Refill(ref _globalTokens, ref _globalLast, GlobalMaxBytesPerSecond.Value, now);
        }

        if (jobMaxBytesPerSecond is > 0)
        {
            if (!_jobs.TryGetValue(jobId, out var bucket))
            {
                bucket = new Bucket { Tokens = jobMaxBytesPerSecond.Value, Last = now };
                _jobs[jobId] = bucket;
            }

            var jobWait = ConsumeBucket(ref bucket.Tokens, ref bucket.Last, jobMaxBytesPerSecond.Value, bytes, now);
            if (jobWait > TimeSpan.Zero)
            {
                if (!skipGlobal && GlobalMaxBytesPerSecond is > 0)
                {
                    _globalTokens += bytes;
                }

                return jobWait;
            }
        }

        if (IsIdleThrottleActive() && IdleThrottleBytesPerSecond is > 0)
        {
            var idleWait = ConsumeBucket(ref _idleTokens, ref _idleLast, IdleThrottleBytesPerSecond.Value, bytes, now);
            if (idleWait > TimeSpan.Zero)
            {
                if (!skipGlobal && GlobalMaxBytesPerSecond is > 0)
                {
                    _globalTokens += bytes;
                }

                if (jobMaxBytesPerSecond is > 0 && _jobs.TryGetValue(jobId, out var jobBucket))
                {
                    jobBucket.Tokens += bytes;
                }

                return idleWait;
            }
        }
        else if (IdleThrottleBytesPerSecond is > 0)
        {
            Refill(ref _idleTokens, ref _idleLast, IdleThrottleBytesPerSecond.Value, now);
        }

        _windowBytes += bytes;
        return TimeSpan.Zero;
    }

    private void RefreshLoad()
    {
        try
        {
            MachineLoad.Refresh();
        }
        catch
        {
            // stay with last busy flag
        }
    }

    public bool IsIdleThrottleCapping()
    {
        RefreshLoad();
        return IsIdleThrottleActive();
    }

    public double? EffectiveCapBytesPerSecond(double? jobMaxBytesPerSecond)
    {
        double? cap = null;
        Consider(ref cap, jobMaxBytesPerSecond);
        Consider(ref cap, GlobalMaxBytesPerSecond);
        if (IsIdleThrottleCapping())
        {
            Consider(ref cap, IdleThrottleBytesPerSecond);
        }

        return cap;
    }

    public bool IsNearCap(double measuredBytesPerSecond, double? jobMaxBytesPerSecond, double slack = 0.92)
    {
        var cap = EffectiveCapBytesPerSecond(jobMaxBytesPerSecond);
        return cap is > 0 && measuredBytesPerSecond >= cap.Value * slack;
    }

    private bool IsIdleThrottleActive() =>
        IdleThrottleBytesPerSecond is > 0 && MachineLoad.IsBusy;

    private static void Consider(ref double? cap, double? value)
    {
        if (value is > 0 && (cap is null || value < cap))
        {
            cap = value;
        }
    }

    private bool ShouldSkipGlobalThrottle(long now)
    {
        if (GlobalMinBytesPerSecond is not > 0)
        {
            return false;
        }

        var elapsed = ToSeconds(now - _windowStart);
        if (elapsed <= 0)
        {
            return false;
        }

        var rate = _windowBytes / elapsed;
        return rate < GlobalMinBytesPerSecond.Value;
    }

    private void RefillWindow(long now)
    {
        if (_windowStart == 0)
        {
            _windowStart = now;
            return;
        }

        if (ToSeconds(now - _windowStart) >= 1.0)
        {
            _windowBytes = 0;
            _windowStart = now;
        }
    }

    private static TimeSpan ConsumeBucket(ref double tokens, ref long last, double rate, int bytes, long now)
    {
        Refill(ref tokens, ref last, rate, now);
        if (tokens >= bytes)
        {
            tokens -= bytes;
            return TimeSpan.Zero;
        }

        var missing = bytes - tokens;
        return TimeSpan.FromSeconds(missing / rate);
    }

    private static void Refill(ref double tokens, ref long last, double rate, long now)
    {
        if (last == 0)
        {
            last = now;
            tokens = rate;
            return;
        }

        var elapsed = ToSeconds(now - last);
        if (elapsed <= 0)
        {
            return;
        }

        tokens = Math.Min(rate * 2, tokens + elapsed * rate);
        last = now;
    }

    private static long StopwatchTimestamp() => System.Diagnostics.Stopwatch.GetTimestamp();

    private static double ToSeconds(long ticks) =>
        (double)ticks / System.Diagnostics.Stopwatch.Frequency;

    private sealed class Bucket
    {
        public double Tokens;
        public long Last;
    }
}
