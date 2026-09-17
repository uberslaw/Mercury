using System.Diagnostics;

namespace Mercury;

public sealed class PauseGate
{
    private volatile int _paused;
    private volatile int _pauseAfterFile;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private string? _filePath;
    private long _fileSize;
    private long _fileCopied;
    private long _fileStart;

    public PauseGate? Parent { get; set; }

    public bool IsPaused => _paused != 0;

    public bool IsEffectivelyPaused => IsPaused || (Parent?.IsEffectivelyPaused ?? false);

    public bool PauseAfterFileRequested => _pauseAfterFile != 0;

    public string? CurrentFilePath => _filePath;

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
        _filePath = relativePath;
        _fileSize = size;
        Interlocked.Exchange(ref _fileCopied, 0);
        Interlocked.Exchange(ref _fileStart, Stopwatch.GetTimestamp());
    }

    public void AddFileBytes(long count) => Interlocked.Add(ref _fileCopied, count);

    public void EndFile()
    {
        _filePath = null;
        _fileSize = 0;
        Interlocked.Exchange(ref _fileCopied, 0);
        Interlocked.Exchange(ref _fileStart, 0);
    }

    /// <summary>
    /// Remaining time for the file in flight. Zero = no file / already done.
    /// Null = file in flight but speed is unknown.
    /// </summary>
    public TimeSpan? RemainingFileEta(double bytesPerSecond)
    {
        if (string.IsNullOrEmpty(_filePath) || _fileSize <= 0)
        {
            return TimeSpan.Zero;
        }

        var remaining = _fileSize - Interlocked.Read(ref _fileCopied);
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
