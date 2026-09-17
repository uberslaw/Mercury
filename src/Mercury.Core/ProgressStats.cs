namespace Mercury;

public readonly record struct StatPair(string Key, string Value)
{
    public static StatPair Empty { get; } = new("", "");

    public bool HasValue => !string.IsNullOrWhiteSpace(Key);

    public string Display => HasValue ? $"{Key}: {Value}" : "";
}

public sealed class ProgressStats
{
    public static ProgressStats Idle { get; } = From(new JobProgress());

    public StatPair Job { get; init; } = new("Job", "1 of 1");
    public StatPair Files { get; init; } = new("Files", "0/0");
    public StatPair Bytes { get; init; } = new("Bytes", "0 B / 0 B");
    public StatPair Speed { get; init; } = new("Speed", "0 B/s");
    public StatPair Eta { get; init; } = new("ETA", "—");
    public StatPair PauseAfter { get; init; } = StatPair.Empty;
    public StatPair Stage { get; init; } = StatPair.Empty;
    public StatPair Elapsed { get; init; } = StatPair.Empty;
    public StatPair ThisStage { get; init; } = StatPair.Empty;

    public static ProgressStats From(
        JobProgress e,
        DateTimeOffset? now = null,
        bool includeStage = true,
        JobProgress? overall = null,
        int jobIndex = 1,
        int jobCount = 1,
        StatPair? pauseAfter = null)
    {
        var clock = now ?? DateTimeOffset.UtcNow;
        var live = e.Status is JobStatus.Preparing or JobStatus.Enumerating or JobStatus.Copying
            or JobStatus.Verifying or JobStatus.Paused or JobStatus.PausedOutsideHours
            || string.Equals(e.Message, "Writing rundown…", StringComparison.Ordinal)
            || string.Equals(e.Message, "Retrying deferred files", StringComparison.Ordinal);
        var showStage = includeStage && live && e.StageIndex > 0 && e.StageCount > 0 && !string.IsNullOrWhiteSpace(e.StageName);
        var stageName = string.Equals(e.Message, "Retrying deferred files", StringComparison.Ordinal)
            ? "Retrying deferred files"
            : e.StageName;
        var stage = showStage
            ? new StatPair("Stage", $"{e.StageIndex} of {e.StageCount} — {stageName}")
            : StatPair.Empty;
        var elapsed = includeStage && live && e.StartedUtc is not null
            ? new StatPair("Elapsed", ByteFormatter.Duration(e.ElapsedAt(clock)))
            : StatPair.Empty;
        var thisStage = includeStage && live && e.StartedUtc is not null
            ? new StatPair("This stage", ByteFormatter.Duration(e.StageElapsedAt(clock)))
            : StatPair.Empty;

        var count = Math.Max(1, jobCount);
        var index = Math.Clamp(jobIndex, 1, count);
        var files = ProgressHeader.ShowOverall(jobCount) && overall is not null
            ? new StatPair("Files", $"Current {e.FilesCopied}/{e.FilesTotal}  Overall {overall.FilesCopied}/{overall.FilesTotal}")
            : new StatPair("Files", $"{e.FilesCopied}/{e.FilesTotal}");

        var rate = ByteFormatter.EffectiveRate(e.BytesPerSecond, e.BytesCopied, e.ElapsedAt(clock));
        var eta = e.Eta;
        if (eta is null && rate >= 1 && e.BytesTotal > e.BytesCopied)
        {
            eta = TimeSpan.FromSeconds((e.BytesTotal - e.BytesCopied) / rate);
        }

        return new ProgressStats
        {
            Job = new StatPair("Job", $"{index} of {count}"),
            Files = files,
            Bytes = new StatPair("Bytes", $"{ByteFormatter.ToString(e.BytesCopied)} / {ByteFormatter.ToString(e.BytesTotal)}"),
            Speed = new StatPair("Speed", ByteFormatter.Speed(rate)),
            Eta = new StatPair("ETA", ByteFormatter.Eta(eta)),
            PauseAfter = pauseAfter ?? StatPair.Empty,
            Stage = stage,
            Elapsed = elapsed,
            ThisStage = thisStage
        };
    }
}
