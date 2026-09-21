using System.Globalization;

namespace Mercury;

public readonly record struct StatPair(string Key, string Value)
{
    public static StatPair Empty { get; } = new("", "");

    public bool HasValue => !string.IsNullOrWhiteSpace(Key);

    public string Display => HasValue ? $"{Key}: {Value}" : "";
}

public sealed class ProgressStats
{
    public static ProgressStats Idle { get; } = new()
    {
        Job = StatPair.Empty,
        Files = StatPair.Empty,
        Bytes = StatPair.Empty,
        Speed = StatPair.Empty,
        Eta = StatPair.Empty,
        PauseAfter = StatPair.Empty,
        Stage = StatPair.Empty,
        Elapsed = StatPair.Empty,
        ThisStage = StatPair.Empty,
        Types = StatPair.Empty,
        File = StatPair.Empty
    };

    public StatPair Job { get; init; } = StatPair.Empty;
    public StatPair Files { get; init; } = StatPair.Empty;
    public StatPair Bytes { get; init; } = StatPair.Empty;
    public StatPair Speed { get; init; } = StatPair.Empty;
    public StatPair Eta { get; init; } = StatPair.Empty;
    public StatPair PauseAfter { get; init; } = StatPair.Empty;
    public StatPair Stage { get; init; } = StatPair.Empty;
    public StatPair Elapsed { get; init; } = StatPair.Empty;
    public StatPair ThisStage { get; init; } = StatPair.Empty;
    public StatPair Types { get; init; } = StatPair.Empty;
    public StatPair OverallFiles { get; init; } = StatPair.Empty;
    public StatPair File { get; init; } = StatPair.Empty;

    public IReadOnlyList<StatPair> TableCells
    {
        get
        {
        StatPair[] all =
            [
                Job, Stage, File, Elapsed, ThisStage, Files, OverallFiles, Bytes, Speed, Eta, PauseAfter, Types
            ];
            return all.Where(p => p.HasValue).ToArray();
        }
    }

    public IReadOnlyList<string> LayoutKeys =>
        TableCells.Select(p => p.Key).Where(k => ProgressHeader.LayoutKeys.Contains(k)).ToArray();

    public static ProgressStats From(
        JobProgress e,
        DateTimeOffset? now = null,
        bool includeStage = true,
        JobProgress? overall = null,
        int jobIndex = 1,
        int jobCount = 1,
        StatPair? pauseAfter = null,
        bool megabits = false)
    {
        var clock = now ?? DateTimeOffset.UtcNow;
        var live = e.Status is JobStatus.Preparing or JobStatus.Enumerating or JobStatus.Copying
            or JobStatus.Verifying or JobStatus.Paused or JobStatus.PausedOutsideHours
            || e.IsRundownStage
            || string.Equals(e.Message, "Retrying deferred files", StringComparison.Ordinal);
        var stageName = string.Equals(e.Message, "Retrying deferred files", StringComparison.Ordinal)
            ? "Retrying deferred files"
            : e.StageName;
        var stageValue = e.StageIndex > 0 && e.StageCount > 0 && !string.IsNullOrWhiteSpace(stageName)
            ? $"{e.StageIndex} of {e.StageCount} — {stageName}"
            : ProgressHeader.DashOr(stageName);
        var stage = includeStage
            ? new StatPair("Stage", stageValue)
            : StatPair.Empty;
        var elapsedClock = e.EndedUtc ?? clock;
        var elapsed = includeStage && e.StartedUtc is not null
            ? new StatPair("Elapsed", ByteFormatter.Duration(e.ElapsedAt(elapsedClock)))
            : includeStage
                ? new StatPair("Elapsed", "—")
                : StatPair.Empty;
        var thisStage = includeStage
            ? new StatPair("This stage", live && e.StageStartedUtc is not null
                ? ByteFormatter.Duration(e.StageElapsedAt(clock))
                : "—")
            : StatPair.Empty;

        var count = Math.Max(1, jobCount);
        var index = Math.Clamp(jobIndex, 1, count);
        var jobPair = count > 1
            ? new StatPair("Job", $"{index} of {count}")
            : StatPair.Empty;
        var hasCounts = e.FilesTotal > 0 || e.FilesCopied > 0;
        var files = new StatPair("Files", hasCounts ? $"{e.FilesCopied}/{e.FilesTotal}" : "—");
        var file = new StatPair("File", ProgressHeader.CurrentFileDisplay(e.CurrentFile, e.CurrentFileBytesCopied, e.CurrentFileBytesTotal));
        var overallFiles = ProgressHeader.ShowOverall(jobCount) && overall is not null
            ? new StatPair("Overall files", $"{overall.FilesCopied}/{overall.FilesTotal}")
            : StatPair.Empty;

        var rundown = e.IsRundownStage;
        var rate = ByteFormatter.EffectiveRate(e.BytesPerSecond, e.BytesCopied, e.ElapsedAt(elapsedClock));
        var eta = e.Eta;
        if (live && !rundown && eta is null && rate >= 1 && e.BytesTotal > e.BytesCopied)
        {
            eta = TimeSpan.FromSeconds((e.BytesTotal - e.BytesCopied) / rate);
        }

        var speedPair = new StatPair("Speed",
            !live ? "—"
            : rundown ? FormatRundownSpeed(e.RundownPerSecond)
            : ByteFormatter.Speed(rate, megabits));
        var etaPair = new StatPair("ETA",
            !live ? "—"
            : rundown && eta is null ? "…"
            : ByteFormatter.Eta(eta));

        var hasBytes = e.BytesTotal > 0 || e.BytesCopied > 0;
        var bytes = new StatPair("Bytes",
            hasBytes
                ? $"{ByteFormatter.ToString(e.BytesCopied)} / {ByteFormatter.ToString(e.BytesTotal)}"
                : "—");

        return new ProgressStats
        {
            Job = jobPair,
            Files = files,
            OverallFiles = overallFiles,
            Bytes = bytes,
            Speed = speedPair,
            Eta = etaPair,
            PauseAfter = pauseAfter ?? StatPair.Empty,
            Stage = stage,
            File = file,
            Elapsed = elapsed,
            ThisStage = thisStage,
            Types = string.IsNullOrWhiteSpace(e.TypeSummary)
                ? StatPair.Empty
                : new StatPair("Types", e.TypeSummary)
        };
    }

    private static string FormatRundownSpeed(double filesPerSecond)
    {
        if (filesPerSecond < 0.05)
        {
            return "…";
        }

        if (filesPerSecond >= 10)
        {
            return filesPerSecond.ToString("0", CultureInfo.InvariantCulture) + " files/s";
        }

        return filesPerSecond.ToString("0.0", CultureInfo.InvariantCulture) + " files/s";
    }
}
