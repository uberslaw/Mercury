namespace Mercury;

public sealed class UnbufferedIoPolicy
{
    public static UnbufferedIoPolicy Default { get; } = new();

    /// <summary>Any file this large counts as sequential payload and is eligible after a winning probe.</summary>
    public long LargeFileBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>Target sample for buffered vs unbuffered (capped at half the file).</summary>
    public long ProbeSampleBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>Video / ISO / archive / audio this large may use unbuffered after the probe wins.</summary>
    public long SequentialApplyBytes { get; init; } = 64L * 1024 * 1024;

    public long MinProbeSampleBytes { get; init; } = 8L * 1024 * 1024;

    public double ThrottleSlack { get; init; } = 0.92;
}

public sealed class UnbufferedIoSession
{
    private double? _bufferedBytesPerSecond;

    public UnbufferedIoSession(JobOptions options, PayloadInventory inventory, UnbufferedIoPolicy? policy = null)
    {
        ForceOn = options.UnbufferedIo;
        Inventory = inventory;
        Policy = policy ?? UnbufferedIoPolicy.Default;
        if (ForceOn)
        {
            ProbeCompleted = true;
            UseUnbuffered = true;
            SkipReason = "forced";
        }
        else if (!inventory.ShouldProbeUnbuffered(Policy))
        {
            ProbeCompleted = true;
            UseUnbuffered = false;
            SkipReason = "no large sequential payload";
        }
    }

    public bool ForceOn { get; }
    public PayloadInventory Inventory { get; }
    public UnbufferedIoPolicy Policy { get; }
    public bool ProbeCompleted { get; private set; }
    public bool UseUnbuffered { get; private set; }
    public string? SkipReason { get; private set; }
    public double? BufferedBytesPerSecond => _bufferedBytesPerSecond;
    public double? UnbufferedBytesPerSecond { get; private set; }

    public static UnbufferedIoSession Create(
        JobOptions options,
        PayloadInventory inventory,
        BandwidthBudget budget,
        UnbufferedIoPolicy? policy = null)
    {
        var session = new UnbufferedIoSession(options, inventory, policy);
        if (!session.ForceOn && !session.ProbeCompleted && budget.IsIdleThrottleCapping())
        {
            session.Skip("idle throttle is capping");
        }

        return session;
    }

    public bool IsEligible(string path, long size)
    {
        if (ForceOn)
        {
            return true;
        }

        if (size >= Policy.LargeFileBytes)
        {
            return true;
        }

        var kind = FileClassifier.FromExtension(path);
        return FileClassifier.IsSequentialKind(kind) && size >= Policy.SequentialApplyBytes;
    }

    public bool UseUnbufferedFor(string path, long size)
    {
        if (ForceOn)
        {
            return true;
        }

        if (!IsEligible(path, size) || !ProbeCompleted)
        {
            return false;
        }

        return UseUnbuffered;
    }

    public bool NeedsBufferedSample(string path, long size) =>
        !ForceOn && !ProbeCompleted && _bufferedBytesPerSecond is null && IsEligible(path, size);

    public bool NeedsUnbufferedSample(string path, long size) =>
        !ForceOn && !ProbeCompleted && _bufferedBytesPerSecond is not null && IsEligible(path, size);

    public long SampleBytes(long fileSize, int sectorSize)
    {
        var sector = Math.Max(512, sectorSize);
        var target = Policy.ProbeSampleBytes;
        var half = Math.Max(sector, fileSize / 2);
        var sample = Math.Min(target, half);
        if (sample < Policy.MinProbeSampleBytes && fileSize >= Policy.MinProbeSampleBytes * 2)
        {
            sample = Policy.MinProbeSampleBytes;
        }

        sample = sample / sector * sector;
        if (sample <= 0 && fileSize >= sector)
        {
            sample = sector;
        }

        return sample;
    }

    public void NoteBuffered(double bytesPerSecond) =>
        _bufferedBytesPerSecond = bytesPerSecond;

    public void Skip(string reason)
    {
        ProbeCompleted = true;
        UseUnbuffered = false;
        SkipReason = reason;
    }

    public string Complete(double bufferedBps, double unbufferedBps)
    {
        _bufferedBytesPerSecond = bufferedBps;
        UnbufferedBytesPerSecond = unbufferedBps;
        ProbeCompleted = true;
        UseUnbuffered = unbufferedBps > bufferedBps;
        SkipReason = null;
        return FormatProbeLine(bufferedBps, unbufferedBps, UseUnbuffered);
    }

    public void UnbufferedUnavailable()
    {
        ProbeCompleted = true;
        UseUnbuffered = false;
        SkipReason = "unbuffered I/O failed; staying buffered";
    }

    public static string FormatProbeLine(double bufferedBps, double unbufferedBps, bool useUnbuffered)
    {
        var winner = useUnbuffered ? "unbuffered" : "buffered";
        return
            $"Unbuffered probe: buffered {FormatMBps(bufferedBps)} vs unbuffered {FormatMBps(unbufferedBps)} → using {winner}";
    }

    public string StartupLine()
    {
        if (ForceOn)
        {
            return "Unbuffered I/O forced on (/J).";
        }

        if (SkipReason == "no large sequential payload")
        {
            return "Unbuffered probe skipped — no large sequential payload (video, ISO, VHD, or any file ≥ 256 MB).";
        }

        if (SkipReason is not null)
        {
            return $"Unbuffered probe skipped — {SkipReason}.";
        }

        return "Unbuffered I/O: auto-probe on large sequential files. Small files stay buffered.";
    }

    private static string FormatMBps(double bytesPerSecond)
    {
        var mb = bytesPerSecond / (1024d * 1024d);
        var format = mb >= 100 ? "0" : mb >= 10 ? "0.0" : "0.##";
        return mb.ToString(format, System.Globalization.CultureInfo.InvariantCulture) + " MB/s";
    }
}
