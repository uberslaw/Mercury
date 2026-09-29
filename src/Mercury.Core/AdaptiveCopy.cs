using System.Text.Json;

namespace Mercury;

internal enum AdaptiveCopyMode
{
    Sequential = 1,
    Files2 = 2,
    Files4 = 4,
    Ranges2 = 8,
    Files8 = 16
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
    public bool FellOff { get; init; }
    /// <summary>User picked a width in the header. Do not let a speed cap collapse it.</summary>
    public bool Manual { get; init; }
}

internal static class AdaptiveCopyPolicy
{
    public const long LargeFileBytes = 256L * 1024 * 1024;
    public const long ProbeBytes = 128L * 1024 * 1024;
    public const long ProbeMinPendingBytes = 32L * 1024 * 1024;
    public const long PortionBytes = 64L * 1024 * 1024;
    /// <summary>Bytes of the chosen mode before its rate is the falloff baseline, and before a later chunk can drop width.</summary>
    public const long SustainBytes = 128L * 1024 * 1024;
    public const long FalloffBytes = SustainBytes;
    public const double WinRatio = 1.15;
    public const double SpeedDropRatio = 0.70;
    public const int MaxWorkers = 4;
    /// <summary>Manual “8 files” only. The probe never uses this width.</summary>
    public const int ManualEightWorkers = 8;
    public const int VerifyBatchSize = 8;
    public const double ProbeMaxSeconds = 8;
    public const double ProbeMinSeconds = 5;
    /// <summary>One-stream probe rate this many times a later sample is a cache burst, not a baseline.</summary>
    public const double ProbeBurstRatio = 2;
    public const int HeartbeatSeconds = 30;
    public const int CopyModePollSeconds = 1;

    public const string TestingStatus = "Testing copy speed…";
    public const string CopyingTwoFiles = "Copying 2 files";
    public const string CopyingFourFiles = "Copying 4 files";
    public const string CopyingEightFiles = "Copying 8 files";
    public const string CopyingTwoRanges = "Copying 2 ranges";
    public const string CopyingOneStream = "Copying one stream";
    public const string OneStreamSlowedDown = "One stream — parallel copy slowed down";

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

    public static string ChooseWhy(IReadOnlyDictionary<AdaptiveCopyMode, double> bytesPerSecond, AdaptiveCopyMode chosen)
    {
        if (chosen == AdaptiveCopyMode.Sequential
            || !bytesPerSecond.TryGetValue(AdaptiveCopyMode.Sequential, out var baseline)
            || baseline < 1
            || !bytesPerSecond.TryGetValue(chosen, out var rate)
            || rate < baseline * WinRatio)
        {
            return "parallel was not 15% faster than one stream";
        }

        return "15% faster than one stream";
    }

    /// <summary>
    /// A few-second probe, or a one-stream sample far above a later sample, is a cache burst.
    /// Falloff never uses that number; it uses the sustained rate of the chosen mode.
    /// </summary>
    public static bool TrustProbeBaseline(double probeSeconds, double oneStreamBps, IReadOnlyList<ProbeSample> samples)
    {
        if (probeSeconds < ProbeMinSeconds || oneStreamBps < 1)
        {
            return false;
        }

        foreach (var sample in samples)
        {
            if (sample.Mode == AdaptiveCopyMode.Sequential || sample.BytesPerSecond < 1)
            {
                continue;
            }

            if (oneStreamBps > sample.BytesPerSecond * ProbeBurstRatio)
            {
                return false;
            }
        }

        return true;
    }

    public static string StatusFor(AdaptiveCopyMode mode, bool fellOff) => mode switch
    {
        AdaptiveCopyMode.Files8 => CopyingEightFiles,
        AdaptiveCopyMode.Files4 => CopyingFourFiles,
        AdaptiveCopyMode.Files2 => CopyingTwoFiles,
        AdaptiveCopyMode.Ranges2 => CopyingTwoRanges,
        _ => fellOff ? OneStreamSlowedDown : CopyingOneStream
    };

    public static string WithOtherFiles(string? primary, int others)
    {
        if (string.IsNullOrWhiteSpace(primary) || others <= 0)
        {
            return primary ?? "";
        }

        return others == 1
            ? primary + " and 1 other file"
            : primary + " and " + others.ToString(System.Globalization.CultureInfo.InvariantCulture) + " other files";
    }

    public static string ProbeFileLabel(IReadOnlyList<FileRecord> files)
    {
        var ranked = files.Where(f => f.Size > 0).OrderByDescending(f => f.Size).Take(MaxWorkers).ToList();
        if (ranked.Count == 0)
        {
            return "";
        }

        return WithOtherFiles(ranked[0].RelativePath, ranked.Count - 1);
    }

    public static double Rate(long bytes, double seconds)
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

    public static int Width(AdaptiveCopyMode mode) => mode switch
    {
        AdaptiveCopyMode.Files2 => 2,
        AdaptiveCopyMode.Files4 => MaxWorkers,
        AdaptiveCopyMode.Files8 => ManualEightWorkers,
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
        var threadsWon = input.Mode is AdaptiveCopyMode.Files2 or AdaptiveCopyMode.Files4 or AdaptiveCopyMode.Files8;
        var honorCap = input.Capped && !input.Manual;
        var width = honorCap ? 1 : AdaptiveCopyPolicy.Width(input.Mode);
        var packLeft = input.PackBytesRemaining > 0;
        var stripe = !honorCap
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

        if (honorCap)
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
        var label = Label(width, stripe, packing, verify > 0 && !stripe && width <= 1, input.FellOff);
        return new BalancePlan(Math.Max(1, width), stripe, portion, runPack, verify, label);
    }

    private static string Label(int width, bool stripe, bool packing, bool verifying, bool fellOff)
    {
        if (fellOff && !stripe)
        {
            return AdaptiveCopyPolicy.OneStreamSlowedDown;
        }

        if (stripe)
        {
            return packing ? "Copying 2 ranges and packing" : AdaptiveCopyPolicy.CopyingTwoRanges;
        }

        if (width >= 8)
        {
            return AdaptiveCopyPolicy.CopyingEightFiles;
        }

        if (width >= 4)
        {
            return AdaptiveCopyPolicy.CopyingFourFiles;
        }

        if (width == 2)
        {
            return AdaptiveCopyPolicy.CopyingTwoFiles;
        }

        if (packing)
        {
            return "Copying and packing";
        }

        if (verifying)
        {
            return "Copying and verifying";
        }

        return AdaptiveCopyPolicy.CopyingOneStream;
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

public enum HeaderCopyMode
{
    FollowSaved = 0,
    Adaptive = 1,
    OneStream = 2,
    Files2 = 3,
    Files4 = 4,
    Ranges2 = 5,
    Files8 = 6
}

/// <summary>Header checkboxes on a fixed row under the Speed column. One mode is on. Not the saved job option.</summary>
public static class HeaderCopy
{
    public static bool IsManual(HeaderCopyMode mode) =>
        mode is HeaderCopyMode.OneStream or HeaderCopyMode.Files2 or HeaderCopyMode.Files4 or HeaderCopyMode.Files8 or HeaderCopyMode.Ranges2;

    public static bool UsesAdaptive(HeaderCopyMode mode, bool savedAdaptive) => mode switch
    {
        HeaderCopyMode.Adaptive => true,
        HeaderCopyMode.FollowSaved => savedAdaptive,
        _ => false
    };

    public static bool ShouldProbe(HeaderCopyMode mode, bool savedAdaptive, bool pendingLargeEnough, bool remembered)
    {
        if (!UsesAdaptive(mode, savedAdaptive) || remembered || !pendingLargeEnough)
        {
            return false;
        }

        return true;
    }

    internal static AdaptiveCopyMode EngineMode(HeaderCopyMode mode) => mode switch
    {
        HeaderCopyMode.Files2 => AdaptiveCopyMode.Files2,
        HeaderCopyMode.Files4 => AdaptiveCopyMode.Files4,
        HeaderCopyMode.Files8 => AdaptiveCopyMode.Files8,
        HeaderCopyMode.Ranges2 => AdaptiveCopyMode.Ranges2,
        _ => AdaptiveCopyMode.Sequential
    };

    public static string Name(HeaderCopyMode mode) => mode switch
    {
        HeaderCopyMode.OneStream => "one stream",
        HeaderCopyMode.Files2 => "2 files",
        HeaderCopyMode.Files4 => "4 files",
        HeaderCopyMode.Files8 => "8 files",
        HeaderCopyMode.Ranges2 => "2 ranges",
        _ => "adaptive"
    };

    public static string SwitchDetail(HeaderCopyMode from, HeaderCopyMode to)
    {
        if (IsManual(to))
        {
            return $"{Name(from)} → {Name(to)}, adaptive turned off";
        }

        return $"{Name(from)} → adaptive";
    }

    public static string SwitchLine(HeaderCopyMode from, HeaderCopyMode to) =>
        "Copy mode: " + SwitchDetail(from, to) + ".";

    internal static string RunningStatus(AdaptiveCopyMode mode, string? switchDetail, bool fellOff = false)
    {
        var label = AdaptiveCopyPolicy.StatusFor(mode, fellOff);
        return string.IsNullOrEmpty(switchDetail) ? label : label + " — " + switchDetail;
    }

    public static bool TooSmallToSplit(long size) => size < AdaptiveCopyPolicy.LargeFileBytes;

    public static string TooSmallStatus(string relative) =>
        $"One stream — {relative} is too small to split";

    public static string TooSmallLog(string relative) =>
        $"Copy mode: 2 ranges → one stream for {relative}, file is too small to split.";
}

/// <summary>Mutually exclusive header choice. Checking one clears the others.</summary>
public sealed class HeaderCopySelection
{
    private HeaderCopyMode _mode = HeaderCopyMode.Adaptive;

    public HeaderCopyMode Mode => _mode;
    public bool UserPicked { get; private set; }
    public bool Adaptive => _mode == HeaderCopyMode.Adaptive;
    public bool OneStream => _mode == HeaderCopyMode.OneStream;
    public bool Files2 => _mode == HeaderCopyMode.Files2;
    public bool Files4 => _mode == HeaderCopyMode.Files4;
    public bool Files8 => _mode == HeaderCopyMode.Files8;
    public bool Ranges2 => _mode == HeaderCopyMode.Ranges2;

    public int OnCount =>
        (Adaptive ? 1 : 0) + (OneStream ? 1 : 0) + (Files2 ? 1 : 0) + (Files4 ? 1 : 0) + (Files8 ? 1 : 0) + (Ranges2 ? 1 : 0);

    public string? Select(HeaderCopyMode next)
    {
        if (next == HeaderCopyMode.FollowSaved)
        {
            next = HeaderCopyMode.Adaptive;
        }

        if (next == _mode)
        {
            UserPicked = true;
            return null;
        }

        var from = _mode;
        _mode = next;
        UserPicked = true;
        return HeaderCopy.SwitchLine(from, next);
    }

    public bool ReflectSaved(bool savedAdaptive)
    {
        if (UserPicked)
        {
            return false;
        }

        var next = savedAdaptive ? HeaderCopyMode.Adaptive : HeaderCopyMode.OneStream;
        if (_mode == next)
        {
            return false;
        }

        _mode = next;
        return true;
    }

    public void Reset()
    {
        _mode = HeaderCopyMode.Adaptive;
        UserPicked = false;
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
        Action<string, string?>? report,
        CancellationToken cancellationToken)
    {
        if (HeaderCopy.IsManual(job.HeaderCopyMode))
        {
            var forced = HeaderCopy.EngineMode(job.HeaderCopyMode);
            var detail = job.HeaderCopySwitchDetail;
            report?.Invoke(HeaderCopy.RunningStatus(forced, detail), string.IsNullOrEmpty(AdaptiveCopyPolicy.ProbeFileLabel(pending)) ? null : AdaptiveCopyPolicy.ProbeFileLabel(pending));
            var manualUse = true;
            return new AdaptivePlan(forced, 0, manualUse, AdaptiveCopyPolicy.IsCapped(job, budget));
        }

        if (!job.Options.AdaptiveCopy && job.HeaderCopyMode != HeaderCopyMode.Adaptive)
        {
            return new AdaptivePlan(AdaptiveCopyMode.Sequential, 0, false, false);
        }

        var capped = AdaptiveCopyPolicy.IsCapped(job, budget);
        var mode = AdaptiveCopyMode.Sequential;
        double baseline = 0;
        var files = AdaptiveCopyPolicy.ProbeFileLabel(pending);
        if (capped)
        {
            log.Info(job.Id, name, "Adaptive copy: a speed cap is set, so this job stays on one stream.");
            report?.Invoke(AdaptiveCopyPolicy.CopyingOneStream, null);
        }
        else if (AdaptiveCopyMemory.TryLookup(job.SourcePath, job.DestinationPath, out var remembered, out baseline))
        {
            mode = remembered;
            log.Info(job.Id, name, $"Adaptive copy: remembered {Describe(mode)} for this source and destination volume.");
            report?.Invoke(AdaptiveCopyPolicy.StatusFor(mode, fellOff: false), null);
        }
        else if (AdaptiveCopyPolicy.ShouldProbe(pending))
        {
            report?.Invoke(AdaptiveCopyPolicy.TestingStatus, string.IsNullOrEmpty(files) ? null : files);
            var measured = await AdaptiveCopyProbe.MeasureAsync(pending, cancellationToken).ConfigureAwait(false);
            if (HeaderCopy.IsManual(job.HeaderCopyMode))
            {
                var forced = HeaderCopy.EngineMode(job.HeaderCopyMode);
                report?.Invoke(HeaderCopy.RunningStatus(forced, job.HeaderCopySwitchDetail), string.IsNullOrEmpty(files) ? null : files);
                return new AdaptivePlan(forced, 0, true, capped);
            }

            if (measured.Failed || measured.Samples.Count == 0)
            {
                mode = AdaptiveCopyMode.Sequential;
                log.Info(job.Id, name, "Adaptive copy probe failed; one stream.");
            }
            else
            {
                mode = measured.Mode;
                // The probe rate is not the falloff baseline. The first sustained chunk of this mode is.
                baseline = 0;
                AdaptiveCopyMemory.Remember(job.SourcePath, job.DestinationPath, mode, 0);
                log.Info(job.Id, name, AdaptiveCopyLog.ProbeLine(measured.Samples, measured.ElapsedSeconds, mode));
            }

            report?.Invoke(AdaptiveCopyPolicy.StatusFor(mode, fellOff: false), string.IsNullOrEmpty(files) ? null : files);
        }
        else
        {
            report?.Invoke(AdaptiveCopyPolicy.CopyingOneStream, null);
        }

        var use = AdaptiveCopyPolicy.UseBalancer(mode, capped, hasPack, largestPending, pending.Count);
        return new AdaptivePlan(mode, baseline, use, capped);
    }

    public static string Describe(AdaptiveCopyMode mode) => mode switch
    {
        AdaptiveCopyMode.Files2 => "2 files at once",
        AdaptiveCopyMode.Files4 => "4 files at once",
        AdaptiveCopyMode.Files8 => "8 files at once",
        AdaptiveCopyMode.Ranges2 => "2 ranges of a large file",
        _ => "one stream"
    };
}

internal readonly record struct ProbeSample(AdaptiveCopyMode Mode, long Bytes, double Seconds)
{
    public double BytesPerSecond => AdaptiveCopyPolicy.Rate(Bytes, Seconds);
}

internal readonly record struct ProbeResult(
    AdaptiveCopyMode Mode,
    double OneStreamBps,
    IReadOnlyList<ProbeSample> Samples,
    double ElapsedSeconds,
    bool Failed)
{
    public static ProbeResult Empty { get; } = new(AdaptiveCopyMode.Sequential, 0, [], 0, false);
}

internal readonly record struct SustainObservation(
    bool LockedBaseline,
    bool DropToOneStream,
    double LiveBps,
    double ComparisonBps,
    string? LogLine);

/// <summary>
/// Falloff baseline is the first sustained chunk of the chosen mode, after the probe.
/// A later chunk of the same size drops to one stream only when it is about 30% slower.
/// </summary>
internal sealed class AdaptiveSustainTracker
{
    private long _markBytes;
    private double _markSeconds;
    private bool _started;
    private bool _locked;
    private double _baselineBps;
    private bool _dropped;

    public bool BaselineLocked => _locked;
    public double BaselineBps => _baselineBps;
    public bool Dropped => _dropped;

    public void Start(long sessionBytes, double elapsedSeconds)
    {
        _markBytes = sessionBytes;
        _markSeconds = elapsedSeconds;
        _started = true;
        _locked = false;
        _baselineBps = 0;
        _dropped = false;
    }

    public SustainObservation Observe(long sessionBytes, double elapsedSeconds, AdaptiveCopyMode mode)
    {
        if (!_started || _dropped || mode == AdaptiveCopyMode.Sequential)
        {
            return default;
        }

        var chunkBytes = sessionBytes - _markBytes;
        if (chunkBytes < AdaptiveCopyPolicy.SustainBytes)
        {
            return default;
        }

        var live = AdaptiveCopyPolicy.Rate(chunkBytes, elapsedSeconds - _markSeconds);
        if (!_locked)
        {
            _locked = true;
            _baselineBps = live;
            _markBytes = sessionBytes;
            _markSeconds = elapsedSeconds;
            return new SustainObservation(
                true,
                false,
                live,
                live,
                $"Adaptive copy sustained {AdaptiveCopyLog.Mbps(live)} over {ByteFormatter.ToString(chunkBytes)} in {AdaptiveCopyPlanner.Describe(mode)} (this is the baseline).");
        }

        var comparison = _baselineBps;
        _markBytes = sessionBytes;
        _markSeconds = elapsedSeconds;
        if (live > 1 && comparison > 1 && live < comparison * AdaptiveCopyPolicy.SpeedDropRatio)
        {
            _dropped = true;
            return new SustainObservation(false, true, live, comparison, null);
        }

        return new SustainObservation(false, false, live, comparison, null);
    }
}

internal static class AdaptiveCopyLog
{
    public static string Mbps(double bytesPerSecond)
    {
        var mb = bytesPerSecond / (1024d * 1024d);
        var format = Math.Abs(mb) >= 10 ? "0.0" : "0.00";
        return mb.ToString(format, System.Globalization.CultureInfo.InvariantCulture) + " MB/s";
    }

    public static string ProbeLine(IReadOnlyList<ProbeSample> samples, double elapsedSeconds, AdaptiveCopyMode chosen)
    {
        var parts = new List<string>(samples.Count);
        foreach (var sample in samples)
        {
            if (sample.Bytes <= 0 && sample.Seconds <= 0)
            {
                continue;
            }

            parts.Add(
                $"{Short(sample.Mode)} {ByteFormatter.ToString(sample.Bytes)} in {Seconds(sample.Seconds)} ({Mbps(sample.BytesPerSecond)})");
        }

        var rates = new Dictionary<AdaptiveCopyMode, double>();
        double oneStream = 0;
        foreach (var sample in samples)
        {
            rates[sample.Mode] = sample.BytesPerSecond;
            if (sample.Mode == AdaptiveCopyMode.Sequential)
            {
                oneStream = sample.BytesPerSecond;
            }
        }

        var trusted = AdaptiveCopyPolicy.TrustProbeBaseline(elapsedSeconds, oneStream, samples);
        var baseline = trusted
            ? "Falloff uses the sustained rate of the chosen mode, not this probe."
            : elapsedSeconds < AdaptiveCopyPolicy.ProbeMinSeconds
                ? $"Probe {Seconds(elapsedSeconds)} is not the baseline."
                : "One stream is well above a later sample, so the probe is not the baseline.";
        var body = parts.Count == 0 ? "no sample" : string.Join("; ", parts);
        return $"Adaptive copy probe: {body}. Chose {AdaptiveCopyPlanner.Describe(chosen)} ({AdaptiveCopyPolicy.ChooseWhy(rates, chosen)}). {baseline}";
    }

    public static string WidthChange(
        AdaptiveCopyMode from,
        AdaptiveCopyMode to,
        double liveBps,
        double comparisonBps,
        string reason) =>
        $"Adaptive copy: {AdaptiveCopyPlanner.Describe(from)} → {AdaptiveCopyPlanner.Describe(to)}, live {Mbps(liveBps)} vs {Mbps(comparisonBps)}, {reason}.";

    public static string Heartbeat(
        string modeLabel,
        IReadOnlyList<string> paths,
        double windowBps,
        double averageBps)
    {
        string files;
        if (paths.Count == 0)
        {
            files = "no file in flight";
        }
        else
        {
            var shown = paths.Count <= 4 ? paths : paths.Take(4).ToList();
            files = string.Join(", ", shown);
            if (paths.Count > 4)
            {
                files += " +" + (paths.Count - 4).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return $"Adaptive copy: {modeLabel}, {files}, window {Mbps(windowBps)}, job {Mbps(averageBps)}.";
    }

    private static string Short(AdaptiveCopyMode mode) => mode switch
    {
        AdaptiveCopyMode.Files2 => "2 files",
        AdaptiveCopyMode.Files4 => "4 files",
        AdaptiveCopyMode.Files8 => "8 files",
        AdaptiveCopyMode.Ranges2 => "2 ranges",
        _ => "one stream"
    };

    private static string Seconds(double seconds) =>
        seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s";
}

internal static class AdaptiveCopyProbe
{
    public static async Task<ProbeResult> MeasureAsync(
        IReadOnlyList<FileRecord> files,
        CancellationToken cancellationToken)
    {
        var existing = files.Where(f => f.Size > 0 && File.Exists(f.SourcePath)).ToList();
        if (existing.Count == 0)
        {
            return ProbeResult.Empty;
        }

        var sample = Math.Min(AdaptiveCopyPolicy.ProbeBytes, existing.Sum(f => f.Size));
        if (sample < 1024 * 1024)
        {
            return ProbeResult.Empty;
        }

        var dir = Path.Combine(Path.GetTempPath(), "mercury-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var samples = new List<ProbeSample>();
            var rates = new Dictionary<AdaptiveCopyMode, double>();
            var one = await TimeOneStreamAsync(existing, sample, dir, cancellationToken).ConfigureAwait(false);
            samples.Add(one);
            rates[one.Mode] = one.BytesPerSecond;
            if (existing.Count >= 2)
            {
                var two = await TimeFilesAsync(existing, 2, sample, dir, cancellationToken).ConfigureAwait(false);
                samples.Add(two);
                rates[two.Mode] = two.BytesPerSecond;
            }

            if (existing.Count >= 4)
            {
                var four = await TimeFilesAsync(existing, 4, sample, dir, cancellationToken).ConfigureAwait(false);
                samples.Add(four);
                rates[four.Mode] = four.BytesPerSecond;
            }

            if (existing.Any(f => f.Size >= AdaptiveCopyPolicy.LargeFileBytes))
            {
                var ranges = await TimeRangesAsync(existing, sample, dir, cancellationToken).ConfigureAwait(false);
                samples.Add(ranges);
                rates[ranges.Mode] = ranges.BytesPerSecond;
            }

            clock.Stop();
            return new ProbeResult(AdaptiveCopyPolicy.Choose(rates), one.BytesPerSecond, samples, clock.Elapsed.TotalSeconds, false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return ProbeResult.Empty with { Failed = true };
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

    private static async Task<ProbeSample> TimeOneStreamAsync(
        IReadOnlyList<FileRecord> files, long sample, string dir, CancellationToken cancellationToken)
    {
        var file = files.OrderByDescending(f => f.Size).First();
        var dest = Path.Combine(dir, "one.bin");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var copied = await CopySampleAsync(file.SourcePath, 0, Math.Min(sample, file.Size), dest, cancellationToken)
            .ConfigureAwait(false);
        clock.Stop();
        return new ProbeSample(AdaptiveCopyMode.Sequential, copied, clock.Elapsed.TotalSeconds);
    }

    private static async Task<ProbeSample> TimeFilesAsync(
        IReadOnlyList<FileRecord> files, int width, long sample, string dir, CancellationToken cancellationToken)
    {
        var chosen = files.OrderByDescending(f => f.Size).Take(width).ToList();
        var each = Math.Max(1, sample / width);
        var mode = width >= 4 ? AdaptiveCopyMode.Files4 : AdaptiveCopyMode.Files2;
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
        return new ProbeSample(mode, copied.Sum(), clock.Elapsed.TotalSeconds);
    }

    private static async Task<ProbeSample> TimeRangesAsync(
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
        return new ProbeSample(AdaptiveCopyMode.Ranges2, copied.Sum(), clock.Elapsed.TotalSeconds);
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
