using System.Diagnostics;

namespace Mercury;

public sealed class PauseGate
{
    private sealed class Inflight
    {
        public string Path = "";
        public long Size;
        public long Copied;
        public long Start;
        public long StartedUtcTicks;
    }

    private volatile int _paused;
    private volatile int _pauseAfterFile;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly object _filesLock = new();
    private readonly Dictionary<string, Inflight> _inflight = new(StringComparer.OrdinalIgnoreCase);
    private string? _lastPath;
    private string? _filePath;
    private long _fileSize;
    private long _fileCopied;
    private long _fileStart;
    private long _fileStartedUtcTicks;

    public PauseGate? Parent { get; set; }

    public string CopyModeLabel { get; set; } = "";

    public int InFlightCount
    {
        get
        {
            lock (_filesLock)
            {
                return _inflight.Count;
            }
        }
    }

    public bool IsPaused => _paused != 0;

    public bool IsEffectivelyPaused => IsPaused || (Parent?.IsEffectivelyPaused ?? false);

    public bool PauseAfterFileRequested => _pauseAfterFile != 0;

    public string? CurrentFilePath => _filePath;

    public long CurrentFileCopied => Interlocked.Read(ref _fileCopied);

    public long CurrentFileSize => Interlocked.Read(ref _fileSize);

    public DateTimeOffset? CurrentFileStartedUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _fileStartedUtcTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public void Pause()
    {
        Interlocked.Exchange(ref _paused, 1);
    }

    public void Resume()
    {
        if (Interlocked.Exchange(ref _paused, 0) != 0)
        {
            try
            {
                _signal.Release();
            }
            catch (SemaphoreFullException)
            {
                // already signaled
            }
        }
    }

    public void RequestPauseAfterFile() => Interlocked.Exchange(ref _pauseAfterFile, 1);

    public void CancelPauseAfterFile() => Interlocked.Exchange(ref _pauseAfterFile, 0);

    public bool TryApplyPauseAfterFile()
    {
        if (Interlocked.Exchange(ref _pauseAfterFile, 0) == 0)
        {
            return false;
        }

        Pause();
        return true;
    }

    public void BeginFile(string relativePath, long size)
    {
        lock (_filesLock)
        {
            _inflight[relativePath] = new Inflight
            {
                Path = relativePath,
                Size = size,
                Copied = 0,
                Start = Stopwatch.GetTimestamp(),
                StartedUtcTicks = DateTimeOffset.UtcNow.UtcTicks
            };
            _lastPath = relativePath;
            PublishPrimary();
        }
    }

    public void AddFileBytes(long count) => AddFileBytes(count, null);

    public void AddFileBytes(long count, string? path)
    {
        if (count == 0)
        {
            return;
        }

        lock (_filesLock)
        {
            var key = path ?? _lastPath;
            if (key is not null && _inflight.TryGetValue(key, out var file))
            {
                file.Copied += count;
            }
            else if (_inflight.Count == 1)
            {
                foreach (var only in _inflight.Values)
                {
                    only.Copied += count;
                }
            }

            PublishPrimary();
        }
    }

    public void EndFile() => EndFile(null);

    public void EndFile(string? path)
    {
        lock (_filesLock)
        {
            var key = path ?? _lastPath;
            if (key is not null)
            {
                _inflight.Remove(key);
            }

            if (_lastPath is not null && !_inflight.ContainsKey(_lastPath))
            {
                _lastPath = null;
                foreach (var remaining in _inflight.Keys)
                {
                    _lastPath = remaining;
                }
            }

            PublishPrimary();
        }
    }

    private void PublishPrimary()
    {
        Inflight? best = null;
        var bestRemain = long.MinValue;
        foreach (var file in _inflight.Values)
        {
            var remain = file.Size - file.Copied;
            if (best is null || remain > bestRemain || (remain == bestRemain && file.Size > best.Size))
            {
                best = file;
                bestRemain = remain;
            }
        }

        if (best is null)
        {
            _filePath = null;
            Interlocked.Exchange(ref _fileSize, 0);
            Interlocked.Exchange(ref _fileCopied, 0);
            Interlocked.Exchange(ref _fileStart, 0);
            Interlocked.Exchange(ref _fileStartedUtcTicks, 0);
            return;
        }

        _filePath = best.Path;
        Interlocked.Exchange(ref _fileSize, best.Size);
        Interlocked.Exchange(ref _fileCopied, best.Copied);
        Interlocked.Exchange(ref _fileStart, best.Start);
        Interlocked.Exchange(ref _fileStartedUtcTicks, best.StartedUtcTicks);
    }

    /// <summary>
    /// Remaining time for the file in flight. Zero = no file / already done.
    /// Null = file in flight but speed is unknown.
    /// </summary>
    public TimeSpan? RemainingFileEta(double bytesPerSecond)
    {
        var size = Interlocked.Read(ref _fileSize);
        if (string.IsNullOrEmpty(_filePath) || size <= 0)
        {
            return TimeSpan.Zero;
        }

        var remaining = size - Interlocked.Read(ref _fileCopied);
        if (remaining <= 0)
        {
            return TimeSpan.Zero;
        }

        var bps = bytesPerSecond;
        if (bps < 1)
        {
            var started = Interlocked.Read(ref _fileStart);
            var fileElapsed = started == 0
                ? 0
                : (Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency;
            var copied = Interlocked.Read(ref _fileCopied);
            if (copied > 0 && fileElapsed >= 3)
            {
                bps = copied / fileElapsed;
            }
            else
            {
                return null;
            }
        }

        return TimeSpan.FromSeconds(remaining / bps);
    }

    public async Task WaitIfPausedAsync(CancellationToken cancellationToken)
    {
        while (IsEffectivelyPaused)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _signal.WaitAsync(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }
    }
}

public static class RunWindow
{
    public static bool IsInside(TimeOnly now, TimeOnly start, TimeOnly end)
    {
        if (start == end)
        {
            return true;
        }

        if (start < end)
        {
            return now >= start && now < end;
        }

        return now >= start || now < end;
    }

    public static TimeSpan DelayUntilOpen(TimeOnly now, TimeOnly start, TimeOnly end)
    {
        if (IsInside(now, start, end))
        {
            return TimeSpan.Zero;
        }

        var todayOpen = now <= start
            ? start.ToTimeSpan() - now.ToTimeSpan()
            : TimeSpan.FromDays(1) - now.ToTimeSpan() + start.ToTimeSpan();

        return todayOpen < TimeSpan.Zero ? TimeSpan.Zero : todayOpen;
    }
}
