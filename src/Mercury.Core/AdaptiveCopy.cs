using System.Text.Json;

namespace Mercury;

internal enum AdaptiveCopyMode
{
    Sequential = 1,
    Files2 = 2,
    Files4 = 4,
    Ranges2 = 8
}

internal readonly record struct AdaptiveFileCopy(bool Finished, string? Hash, long BytesOnDisk, bool Fallback);

internal readonly record struct AdaptivePlan(
    AdaptiveCopyMode Mode,
    double BaselineBps,
    bool UseBalancer,
    bool Capped);

internal readonly record struct BalancePlan(
    int CopyWidth,
    bool Stripe,
    long? PortionBytes,
    bool RunPack,
    int VerifyBatch,
    string Label);

internal sealed class BalanceInput
{
    public AdaptiveCopyMode Mode { get; init; } = AdaptiveCopyMode.Sequential;
    public bool AdaptiveEnabled { get; init; } = true;
    public bool Capped { get; init; }
    public int PendingFiles { get; init; }
    public long PendingCopyBytes { get; init; }
    public long LargestPendingBytes { get; init; }
    public long PackBytesRemaining { get; init; }
    public bool PackRunning { get; init; }
    public int CopyInFlight { get; init; }
    public int UnverifiedCopied { get; init; }
}

internal static class AdaptiveCopyPolicy
{
    public const long LargeFileBytes = 256L * 1024 * 1024;
    public const long ProbeBytes = 128L * 1024 * 1024;
    public const long ProbeMinPendingBytes = 32L * 1024 * 1024;
    public const long PortionBytes = 64L * 1024 * 1024;
    public const long FalloffBytes = 32L * 1024 * 1024;
    public const double WinRatio = 1.15;
    public const double SpeedDropRatio = 0.70;
    public const int MaxWorkers = 4;
    public const int VerifyBatchSize = 8;
    public const double ProbeMaxSeconds = 8;

    public static bool IsCapped(Job job, BandwidthBudget budget) =>
        job.Options.MaxBytesPerSecond is > 0 || budget.GlobalMaxBytesPerSecond is > 0;

    public static bool ShouldProbe(IReadOnlyList<FileRecord> pending)
    {
        long bytes = 0;
        foreach (var file in pending)
        {
            if (file.Size >= LargeFileBytes)
            {
                return true;
            }

            bytes += Math.Max(0, file.Size - file.BytesCopied);
        }

        return bytes >= ProbeMinPendingBytes;
    }

    public static IReadOnlyList<AdaptiveCopyMode> Candidates(IReadOnlyList<FileRecord> files)
    {
        var list = new List<AdaptiveCopyMode> { AdaptiveCopyMode.Sequential };
        if (files.Count >= 2)
        {
            list.Add(AdaptiveCopyMode.Files2);
        }

        if (files.Count >= 4)
        {
            list.Add(AdaptiveCopyMode.Files4);
        }

        foreach (var file in files)
        {
            if (file.Size >= LargeFileBytes)
            {
                list.Add(AdaptiveCopyMode.Ranges2);
                break;
            }
        }

        return list;
    }

    /// <summary>
    /// Keep a wider mode only when it is at least 15% faster than one stream.
    /// </summary>
    public static AdaptiveCopyMode Choose(IReadOnlyDictionary<AdaptiveCopyMode, double> bytesPerSecond)
    {
        if (!bytesPerSecond.TryGetValue(AdaptiveCopyMode.Sequential, out var baseline) || baseline < 1)
        {
            return AdaptiveCopyMode.Sequential;
        }

        var best = AdaptiveCopyMode.Sequential;
        var bestRate = baseline;
        foreach (var (mode, rate) in bytesPerSecond)
        {
            if (mode == AdaptiveCopyMode.Sequential || rate < 1)
            {
                continue;
            }

            if (rate >= baseline * WinRatio && rate > bestRate)
            {
                best = mode;
                bestRate = rate;
            }
        }

        return best;
    }

    public static int Width(AdaptiveCopyMode mode) => mode switch
    {
        AdaptiveCopyMode.Files2 => 2,
        AdaptiveCopyMode.Files4 => MaxWorkers,
        _ => 1
    };

    public static string VolumeKey(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        var trimmed = path.Trim();
        if (trimmed.Length >= 2 && trimmed[1] == ':')
        {
            return char.ToUpperInvariant(trimmed[0]) + ":";
        }

        var unc = trimmed.StartsWith("\\\\", StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal);
        if (unc)
        {
            var parts = trimmed.TrimStart('\\', '/').Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                return "\\\\" + parts[0] + "\\" + parts[1];
            }
        }

        try
        {
            var root = Path.GetPathRoot(trimmed);
            return string.IsNullOrEmpty(root) ? trimmed : root;
        }
        catch
        {
            return trimmed;
        }
    }

    public static bool UseBalancer(AdaptiveCopyMode mode, bool capped, bool hasPack, long largestPending, int pendingFiles)
    {
        if (Width(mode) > 1 || mode == AdaptiveCopyMode.Ranges2)
        {
            return true;
        }

        if (capped && pendingFiles > 1)
        {
            return true;
        }

        return hasPack && !capped && largestPending >= PortionBytes;
    }
}

internal static class TransferBalancer
{
    public static BalancePlan Next(BalanceInput input)
    {
        var threadsWon = input.Mode is AdaptiveCopyMode.Files2 or AdaptiveCopyMode.Files4;
        var width = input.Capped ? 1 : AdaptiveCopyPolicy.Width(input.Mode);
        var packLeft = input.PackBytesRemaining > 0;
        var stripe = !input.Capped
            && input.Mode == AdaptiveCopyMode.Ranges2
            && input.LargestPendingBytes >= AdaptiveCopyPolicy.LargeFileBytes;
        long? portion = null;
        var runPack = false;

        if (!input.AdaptiveEnabled)
        {
            runPack = packLeft && !input.PackRunning;
            var plain = runPack || input.PackRunning ? "Copying and packing" : "Copying";
            return new BalancePlan(1, false, null, runPack, 0, plain);
        }

        if (input.Capped)
        {
            width = 1;
            stripe = false;
            runPack = packLeft && !input.PackRunning;
        }
        else if (threadsWon && input.PendingFiles > 0)
        {
            // Several files at once beat one stream, so packing waits until that copy finishes.
            runPack = false;
        }
        else if (packLeft && !threadsWon)
        {
            runPack = !input.PackRunning;
            width = 1;
            if (input.LargestPendingBytes >= AdaptiveCopyPolicy.PortionBytes)
            {
                portion = AdaptiveCopyPolicy.PortionBytes;
            }
        }
        else
        {
            runPack = packLeft && !input.PackRunning;
        }

        var verify = 0;
        var copyBusy = input.CopyInFlight > 0 || input.PendingFiles > 0;
        if (input.UnverifiedCopied > 0 && (input.Capped || !copyBusy || (packLeft && !threadsWon)))
        {
            verify = Math.Min(AdaptiveCopyPolicy.VerifyBatchSize, input.UnverifiedCopied);
        }

        var packing = runPack || input.PackRunning;
        var label = Label(width, stripe, packing, verify > 0 && !stripe && width <= 1);
        return new BalancePlan(Math.Max(1, width), stripe, portion, runPack, verify, label);
    }

    private static string Label(int width, bool stripe, bool packing, bool verifying)
    {
        if (stripe)
        {
            return packing ? "Copying 2 ranges and packing" : "Copying 2 ranges";
        }

        if (width >= 4)
        {
            return "Copying 4 files";
        }

        if (width == 2)
        {
            return "Copying 2 files";
        }

        if (packing)
        {
            return "Copying and packing";
        }

        if (verifying)
        {
            return "Copying and verifying";
        }

        return "Copying";
    }
}

internal static class AdaptiveVerifyCache
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, HashSet<string>> Done = new(StringComparer.Ordinal);

    public static void Mark(string jobId, string relativePath)
    {
        lock (Gate)
        {
            if (!Done.TryGetValue(jobId, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Done[jobId] = set;
            }

            set.Add(relativePath);
        }
    }

    public static bool Contains(string jobId, string relativePath)
    {
        lock (Gate)
        {
            return Done.TryGetValue(jobId, out var set) && set.Contains(relativePath);
        }
    }

    public static int Count(string jobId)
    {
        lock (Gate)
        {
            return Done.TryGetValue(jobId, out var set) ? set.Count : 0;
        }
    }

    public static void Clear(string jobId)
    {
        lock (Gate)
        {
            Done.Remove(jobId);
        }
    }
}

internal static class AdaptiveCopyMemory
{
    private static readonly object Gate = new();
    private static string? _path;
    private static List<Entry> _entries = [];

    private sealed class Entry
    {
        public string Source { get; set; } = "";
        public string Dest { get; set; } = "";
        public string Mode { get; set; } = nameof(AdaptiveCopyMode.Sequential);
        public double BaselineBps { get; set; }
    }

    private sealed class FileBody
    {
        public List<Entry> Modes { get; set; } = [];
    }

    public static void Use(string dataRoot)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(dataRoot);
            _path = Path.Combine(dataRoot, "adaptive-copy.json");
            _entries = Load(_path);
        }
    }

    public static bool TryLookup(string? source, string? dest, out AdaptiveCopyMode mode, out double baselineBps)
    {
        mode = AdaptiveCopyMode.Sequential;
        baselineBps = 0;
        var src = AdaptiveCopyPolicy.VolumeKey(source);
        var dst = AdaptiveCopyPolicy.VolumeKey(dest);
        if (src.Length == 0 || dst.Length == 0)
        {
            return false;
        }

        lock (Gate)
        {
            foreach (var entry in _entries)
            {
                if (string.Equals(entry.Source, src, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(entry.Dest, dst, StringComparison.OrdinalIgnoreCase) &&
                    Enum.TryParse<AdaptiveCopyMode>(entry.Mode, out var parsed))
                {
                    mode = parsed;
                    baselineBps = entry.BaselineBps;
                    return true;
                }
            }
        }

        return false;
    }

    public static void Remember(string? source, string? dest, AdaptiveCopyMode mode, double baselineBps)
    {
        var src = AdaptiveCopyPolicy.VolumeKey(source);
        var dst = AdaptiveCopyPolicy.VolumeKey(dest);
        if (src.Length == 0 || dst.Length == 0)
        {
            return;
        }

        lock (Gate)
        {
            _entries.RemoveAll(e =>
                string.Equals(e.Source, src, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.Dest, dst, StringComparison.OrdinalIgnoreCase));
            _entries.Add(new Entry
            {
                Source = src,
                Dest = dst,
                Mode = mode.ToString(),
                BaselineBps = baselineBps
            });
            Save();
        }
    }

    public static void Forget(string? source, string? dest)
    {
        var src = AdaptiveCopyPolicy.VolumeKey(source);
        var dst = AdaptiveCopyPolicy.VolumeKey(dest);
        lock (Gate)
        {
            _entries.RemoveAll(e =>
                string.Equals(e.Source, src, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.Dest, dst, StringComparison.OrdinalIgnoreCase));
            Save();
        }
    }

    private static List<Entry> Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            var body = JsonSerializer.Deserialize<FileBody>(File.ReadAllText(path));
            return body?.Modes ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static void Save()
    {
        if (string.IsNullOrEmpty(_path))
        {
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(new FileBody { Modes = _entries });
            File.WriteAllText(_path, json);
        }
        catch
        {
            // memory is a hint; the next job can probe again
        }
    }
}

internal static class AdaptiveCopyPlanner
{
    public static async Task<AdaptivePlan> PrepareAsync(
        Job job,
        BandwidthBudget budget,
        IReadOnlyList<FileRecord> pending,
        bool hasPack,
        long largestPending,
        IJobLog log,
        string name,
        CancellationToken cancellationToken)
    {
        if (!job.Options.AdaptiveCopy)
        {
            return new AdaptivePlan(AdaptiveCopyMode.Sequential, 0, false, false);
        }

        var capped = AdaptiveCopyPolicy.IsCapped(job, budget);
        var mode = AdaptiveCopyMode.Sequential;
        double baseline = 0;
        if (capped)
        {
            log.Info(job.Id, name, "Adaptive copy: a speed cap is set, so this job stays on one stream.");
        }
        else if (AdaptiveCopyMemory.TryLookup(job.SourcePath, job.DestinationPath, out var remembered, out baseline))
        {
            mode = remembered;
            log.Info(job.Id, name, $"Adaptive copy: remembered {Describe(mode)} for this source and destination volume.");
        }
        else if (AdaptiveCopyPolicy.ShouldProbe(pending))
        {
            log.Info(job.Id, name, "Adaptive copy: testing one stream, several files, and a split large file.");
            var measured = await AdaptiveCopyProbe.MeasureAsync(pending, cancellationToken).ConfigureAwait(false);
            mode = measured.Mode;
            baseline = measured.BaselineBps;
            AdaptiveCopyMemory.Remember(job.SourcePath, job.DestinationPath, mode, baseline);
            log.Info(job.Id, name,
                $"Adaptive copy probe chose {Describe(mode)} (one-stream sample {ByteFormatter.ToString((long)baseline)}/s).");
        }

        var use = AdaptiveCopyPolicy.UseBalancer(mode, capped, hasPack, largestPending, pending.Count);
        return new AdaptivePlan(mode, baseline, use, capped);
    }

    public static string Describe(AdaptiveCopyMode mode) => mode switch
    {
        AdaptiveCopyMode.Files2 => "2 files at once",
        AdaptiveCopyMode.Files4 => "4 files at once",
        AdaptiveCopyMode.Ranges2 => "2 ranges of a large file",
        _ => "one stream"
    };
}

internal static class AdaptiveCopyProbe
{
    public static async Task<(AdaptiveCopyMode Mode, double BaselineBps)> MeasureAsync(
        IReadOnlyList<FileRecord> files,
        CancellationToken cancellationToken)
    {
        var existing = files.Where(f => f.Size > 0 && File.Exists(f.SourcePath)).ToList();
        if (existing.Count == 0)
        {
            return (AdaptiveCopyMode.Sequential, 0);
        }

        var sample = Math.Min(AdaptiveCopyPolicy.ProbeBytes, existing.Sum(f => f.Size));
        if (sample < 1024 * 1024)
        {
            return (AdaptiveCopyMode.Sequential, 0);
        }

        var dir = Path.Combine(Path.GetTempPath(), "mercury-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var measurements = new Dictionary<AdaptiveCopyMode, double>();
            var baseline = await TimeOneStreamAsync(existing, sample, dir, cancellationToken).ConfigureAwait(false);
            measurements[AdaptiveCopyMode.Sequential] = baseline;
            if (existing.Count >= 2)
            {
                measurements[AdaptiveCopyMode.Files2] = await TimeFilesAsync(existing, 2, sample, dir, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (existing.Count >= 4)
            {
                measurements[AdaptiveCopyMode.Files4] = await TimeFilesAsync(existing, 4, sample, dir, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (existing.Any(f => f.Size >= AdaptiveCopyPolicy.LargeFileBytes))
            {
                measurements[AdaptiveCopyMode.Ranges2] = await TimeRangesAsync(existing, sample, dir, cancellationToken)
                    .ConfigureAwait(false);
            }

            return (AdaptiveCopyPolicy.Choose(measurements), baseline);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return (AdaptiveCopyMode.Sequential, 0);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
                // probe temps are disposable
            }
        }
    }

    private static async Task<double> TimeOneStreamAsync(
        IReadOnlyList<FileRecord> files, long sample, string dir, CancellationToken cancellationToken)
    {
        var file = files.OrderByDescending(f => f.Size).First();
        var dest = Path.Combine(dir, "one.bin");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var copied = await CopySampleAsync(file.SourcePath, 0, Math.Min(sample, file.Size), dest, cancellationToken)
            .ConfigureAwait(false);
        clock.Stop();
        return Rate(copied, clock.Elapsed.TotalSeconds);
    }

    private static async Task<double> TimeFilesAsync(
        IReadOnlyList<FileRecord> files, int width, long sample, string dir, CancellationToken cancellationToken)
    {
        var chosen = files.OrderByDescending(f => f.Size).Take(width).ToList();
        var each = Math.Max(1, sample / width);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var tasks = new Task<long>[chosen.Count];
        for (var i = 0; i < chosen.Count; i++)
        {
            var file = chosen[i];
            var dest = Path.Combine(dir, "f" + i + ".bin");
            var count = Math.Min(each, file.Size);
            tasks[i] = CopySampleAsync(file.SourcePath, 0, count, dest, cancellationToken);
        }

        var copied = await Task.WhenAll(tasks).ConfigureAwait(false);
        clock.Stop();
        return Rate(copied.Sum(), clock.Elapsed.TotalSeconds);
    }

    private static async Task<double> TimeRangesAsync(
        IReadOnlyList<FileRecord> files, long sample, string dir, CancellationToken cancellationToken)
    {
        var file = files.OrderByDescending(f => f.Size).First();
        var count = Math.Min(sample, file.Size);
        var mid = count / 2;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var first = CopySampleAsync(file.SourcePath, 0, mid, Path.Combine(dir, "r0.bin"), cancellationToken);
        var second = CopySampleAsync(file.SourcePath, mid, count - mid, Path.Combine(dir, "r1.bin"), cancellationToken);
        var copied = await Task.WhenAll(first, second).ConfigureAwait(false);
        clock.Stop();
        return Rate(copied.Sum(), clock.Elapsed.TotalSeconds);
    }

    private static double Rate(long bytes, double seconds)
    {
        if (bytes <= 0)
        {
            return 0;
        }

        if (seconds < 0.001)
        {
            seconds = 0.001;
        }

        return bytes / seconds;
    }

    private static async Task<long> CopySampleAsync(
        string source, long offset, long count, string dest, CancellationToken cancellationToken)
    {
        var flags = FileOptions.SequentialScan | FileOptions.Asynchronous;
        await using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, flags);
        await using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, flags);
        if (offset > 0)
        {
            src.Seek(offset, SeekOrigin.Begin);
        }

        var buffer = new byte[1024 * 1024];
        var left = count;
        var copied = 0L;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (left > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var elapsed = (System.Diagnostics.Stopwatch.GetTimestamp() - started) / (double)System.Diagnostics.Stopwatch.Frequency;
            if (elapsed >= AdaptiveCopyPolicy.ProbeMaxSeconds && copied > 0)
            {
                break;
            }

            var take = (int)Math.Min(buffer.Length, left);
            var read = await src.ReadAsync(buffer.AsMemory(0, take), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            await dst.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            left -= read;
            copied += read;
        }

        return copied;
    }
}

internal static class AdaptiveCopyRunner
{
    public static async Task RunAsync(
        IReadOnlyList<string> files,
        int width,
        Func<string, CancellationToken, Task> copy,
        PauseGate pause,
        CancellationToken cancellationToken)
    {
        var queue = new Queue<string>(files);
        var active = new List<Task>();
        var limit = Math.Max(1, width);
        while (queue.Count > 0 || active.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pause.PauseAfterFileRequested && active.Count == 0)
            {
                pause.TryApplyPauseAfterFile();
                break;
            }

            if (!pause.PauseAfterFileRequested)
            {
                while (active.Count < limit && queue.Count > 0 && !pause.PauseAfterFileRequested)
                {
                    var path = queue.Dequeue();
                    pause.BeginFile(path, 1);
                    active.Add(RunOneAsync(path, copy, pause, cancellationToken));
                }
            }

            if (active.Count == 0)
            {
                break;
            }

            var done = await Task.WhenAny(active).ConfigureAwait(false);
            active.Remove(done);
            await done.ConfigureAwait(false);
        }
    }

    private static async Task RunOneAsync(
        string path,
        Func<string, CancellationToken, Task> copy,
        PauseGate pause,
        CancellationToken cancellationToken)
    {
        try
        {
            await copy(path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            pause.EndFile(path);
        }
    }
}
