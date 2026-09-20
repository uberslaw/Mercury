using System.Globalization;

namespace Mercury;

public enum RundownHighlight
{
    None,
    Ok,
    Warn,
    Danger
}

public sealed class TransferRundown
{
    public static TransferRundown Empty { get; } = new();

    public bool IsVisible { get; init; }
    public string StartedText { get; init; } = "—";
    public string EndedText { get; init; } = "—";
    public string ElapsedText { get; init; } = "—";
    public string AverageSpeedText { get; init; } = "—";
    public string FilesText { get; init; } = "Source 0  Dest 0";
    public string FoldersText { get; init; } = "Source 0  Dest 0";
    public string MatchText { get; init; } = "";
    public RundownHighlight Highlight { get; init; }
    public string OneLine { get; init; } = "";
    public IReadOnlyList<string> ConsoleLines { get; init; } = [];

    public static TransferRundown From(Job job, DateTimeOffset? now = null)
    {
        if (job.StartedUtc is null)
        {
            return Empty;
        }

        if (job.EndedUtc is null)
        {
            return Live(job, now ?? DateTimeOffset.UtcNow);
        }

        var elapsed = job.EndedUtc.Value - job.StartedUtc.Value;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        var filesMatch = job.SourceFiles == job.DestFiles;
        var foldersMatch = job.SourceFolders == job.DestFolders;
        var countsMatch = filesMatch && foldersMatch;
        var dryRun = job.Options.DryRun;
        var highlight = dryRun
            ? (job.Status == JobStatus.Cancelled ? RundownHighlight.Warn : RundownHighlight.Ok)
            : Classify(job.Status, countsMatch);
        var matchText = dryRun
            ? (job.Status == JobStatus.Cancelled
                ? "Stopped — dry run (nothing written)"
                : "Dry run — enumerated only (nothing written)")
            : MatchMessage(job.Status, filesMatch, foldersMatch, job.SourceFiles, job.DestFiles,
                job.SourceFolders, job.DestFolders);
        var elapsedText = ByteFormatter.Duration(elapsed);
        var speedText = ByteFormatter.Speed(job.AverageBytesPerSecond);
        var filesText = FormatPair(job.SourceFiles, job.DestFiles);
        var foldersText = FormatPair(job.SourceFolders, job.DestFolders);
        var started = FormatLocal(job.StartedUtc.Value);
        var ended = FormatLocal(job.EndedUtc.Value);

        var oneLine = string.Join(" · ", new[]
        {
            elapsedText,
            speedText,
            $"Files: {job.DestFiles}/{job.SourceFiles}",
            matchText
        }.Where(s => !string.IsNullOrWhiteSpace(s)));

        return new TransferRundown
        {
            IsVisible = true,
            StartedText = started,
            EndedText = ended,
            ElapsedText = elapsedText + "  (includes enumeration)",
            AverageSpeedText = speedText + "  (copied + skipped)",
            FilesText = filesText,
            FoldersText = foldersText,
            MatchText = matchText,
            Highlight = highlight,
            OneLine = oneLine,
            ConsoleLines =
            [
                "Transfer rundown",
                $"  Started:  {started}",
                $"  Ended:    {ended}",
                $"  Elapsed:  {elapsedText} (wall-clock, includes enumeration)",
                $"  Average:  {speedText} (copied + skipped)",
                $"  Files:    {filesText}",
                $"  Folders:  {foldersText}",
                $"  {matchText}"
            ]
        };
    }

    public static TransferRundown Live(Job job, DateTimeOffset now)
    {
        if (job.StartedUtc is null)
        {
            return Empty;
        }

        var elapsed = now - job.StartedUtc.Value;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        var elapsedText = ByteFormatter.Duration(elapsed);
        var started = FormatLocal(job.StartedUtc.Value);
        var filesText = job.SourceFiles > 0
            ? string.Create(CultureInfo.InvariantCulture, $"Source {job.SourceFiles}  Dest —")
            : "Source 0  Dest —";
        var foldersText = job.SourceFolders > 0
            ? string.Create(CultureInfo.InvariantCulture, $"Source {job.SourceFolders}  Dest —")
            : "Source 0  Dest —";

        return new TransferRundown
        {
            IsVisible = job.SourceFiles > 0 || job.DestFiles > 0 || job.BytesCopied > 0,
            StartedText = started,
            EndedText = "—",
            ElapsedText = elapsedText,
            AverageSpeedText = "—",
            FilesText = filesText,
            FoldersText = foldersText,
            MatchText = "",
            Highlight = RundownHighlight.None,
            OneLine = elapsedText,
            ConsoleLines = []
        };
    }

    public static void Capture(
        Job job,
        JobJournal journal,
        CopyMapping? mapping,
        IJobLog? log,
        string name,
        CancellationToken cancellationToken = default)
    {
        if (job.StartedUtc is null)
        {
            journal.SaveJob(job);
            return;
        }

        var ended = DateTimeOffset.UtcNow;
        job.EndedUtc = ended;
        var totals = SafeTotals(journal);
        job.SourceFiles = totals.Files;
        job.BytesCopied = totals.DoneBytes;

        var skipTree = job.Options.DryRun
            || job.Status == JobStatus.Cancelled
            || cancellationToken.IsCancellationRequested;

        TreeCounts dest = default;
        TreeCounts sourceFolders = job.SourceFolders > 0
            ? new TreeCounts(job.SourceFiles, job.SourceFolders)
            : default;
        if (!skipTree && mapping is not null && job.Catcher is not null && mapping.SingleFile)
        {
            dest = new TreeCounts(1, 0);
            sourceFolders = new TreeCounts(1, 0);
        }
        else if (!skipTree && mapping is not null && ZipPack.Applies(job, mapping) && job.Catcher is not null)
        {
            dest = ZipPack.CountPacked(ZipPack.ZipPath(mapping));
            try
            {
                sourceFolders = new TreeCounts(totals.Files, ZipPack.FolderCountFromFiles(journal.GetFiles()));
            }
            catch
            {
                sourceFolders = new TreeCounts(totals.Files, 0);
            }
        }
        else if (!skipTree && mapping is not null)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                dest = SourceWalker.CountDest(mapping, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                SaveSummary(job, journal, dest, sourceFolders, ended, log, name, stopped: true);
                throw;
            }
            catch
            {
                dest = default;
            }

            if (sourceFolders.Folders == 0)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    sourceFolders = SourceWalker.CountSource(mapping, job.Options, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    SaveSummary(job, journal, dest, sourceFolders, ended, log, name, stopped: true);
                    throw;
                }
                catch
                {
                    sourceFolders = default;
                }
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            SaveSummary(job, journal, dest, sourceFolders, ended, log, name, stopped: true);
            cancellationToken.ThrowIfCancellationRequested();
        }

        SaveSummary(job, journal, dest, sourceFolders, ended, log, name, stopped: job.Status == JobStatus.Cancelled);
    }

    private static void SaveSummary(
        Job job,
        JobJournal journal,
        TreeCounts dest,
        TreeCounts sourceFolders,
        DateTimeOffset ended,
        IJobLog? log,
        string name,
        bool stopped)
    {
        if (job.Options.DryRun || stopped)
        {
            job.DestFiles = job.Options.DryRun ? 0 : dest.Files;
            job.DestFolders = job.Options.DryRun ? 0 : dest.Folders;
        }
        else
        {
            job.DestFiles = dest.Files;
            job.DestFolders = dest.Folders;
        }

        if (sourceFolders.Folders > 0 || job.SourceFolders == 0)
        {
            job.SourceFolders = sourceFolders.Folders;
        }

        var elapsed = ended - job.StartedUtc!.Value;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        job.AverageBytesPerSecond = AverageBytesPerSecond(job.BytesCopied, elapsed);
        journal.SaveJob(job);

        if (log is null)
        {
            return;
        }

        if (stopped)
        {
            log.Info(job.Id, name, "Stopped — rundown skipped.");
        }

        var display = From(job);
        foreach (var line in display.ConsoleLines)
        {
            log.Info(job.Id, name, line);
        }
    }

    public static double AverageBytesPerSecond(long bytes, TimeSpan elapsed)
    {
        if (bytes < 0)
        {
            bytes = 0;
        }

        if (elapsed.TotalSeconds <= 0)
        {
            return 0;
        }

        return bytes / elapsed.TotalSeconds;
    }

    public static RundownHighlight Classify(JobStatus status, bool countsMatch)
    {
        if (status == JobStatus.Completed && countsMatch)
        {
            return RundownHighlight.Ok;
        }

        if (status == JobStatus.Cancelled && countsMatch)
        {
            return RundownHighlight.Warn;
        }

        return RundownHighlight.Danger;
    }

    public static string MatchMessage(
        JobStatus status,
        bool filesMatch,
        bool foldersMatch,
        int sourceFiles,
        int destFiles,
        int sourceFolders,
        int destFolders)
    {
        if (!filesMatch)
        {
            return $"Mismatch — Dest files {destFiles} vs Source {sourceFiles}";
        }

        if (!foldersMatch)
        {
            return $"Mismatch — Dest folders {destFolders} vs Source {sourceFolders}";
        }

        return status switch
        {
            JobStatus.Completed => "Verified — Source and Dest match",
            JobStatus.Incomplete => "Incomplete — verify failed",
            JobStatus.Cancelled => "Stopped — Source and Dest counts match",
            JobStatus.Failed => "Failed — Source and Dest counts match",
            _ => "Source and Dest counts match"
        };
    }

    public static TransferRundown Combined(IReadOnlyList<Job> jobs, DateTimeOffset? now = null)
    {
        if (jobs.Count == 0)
        {
            return Empty;
        }

        if (jobs.Count == 1)
        {
            return From(jobs[0], now);
        }

        var clock = now ?? DateTimeOffset.UtcNow;
        var startedTimes = jobs.Where(j => j.StartedUtc is not null).Select(j => j.StartedUtc!.Value).ToList();
        if (startedTimes.Count == 0)
        {
            return Empty;
        }

        var started = startedTimes.Min();
        var endedTimes = jobs.Where(j => j.EndedUtc is not null).Select(j => j.EndedUtc!.Value).ToList();
        var allEnded = endedTimes.Count == jobs.Count;
        var ended = allEnded ? endedTimes.Max() : (DateTimeOffset?)null;
        var elapsed = (ended ?? clock) - started;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        var sourceFiles = jobs.Sum(j => j.SourceFiles);
        var destFiles = jobs.Sum(j => j.DestFiles);
        var sourceFolders = jobs.Sum(j => j.SourceFolders);
        var destFolders = jobs.Sum(j => j.DestFolders);
        var bytes = jobs.Sum(j => j.BytesCopied);
        var speed = AverageBytesPerSecond(bytes, elapsed);
        var filesMatch = sourceFiles == destFiles;
        var foldersMatch = sourceFolders == destFolders;
        var worst = jobs.Select(j => j.Status).OrderByDescending(StatusRank).First();
        var highlight = Classify(worst, filesMatch && foldersMatch);
        var matchText = jobs.Count + " jobs · " + MatchMessage(worst, filesMatch, foldersMatch, sourceFiles, destFiles, sourceFolders, destFolders);

        return new TransferRundown
        {
            IsVisible = true,
            StartedText = FormatLocal(started),
            EndedText = ended is { } e ? FormatLocal(e) : "—",
            ElapsedText = ByteFormatter.Duration(elapsed),
            AverageSpeedText = ByteFormatter.Speed(speed),
            FilesText = FormatPair(sourceFiles, destFiles),
            FoldersText = FormatPair(sourceFolders, destFolders),
            MatchText = matchText,
            Highlight = highlight,
            OneLine = string.Join(" · ", new[]
            {
                ByteFormatter.Duration(elapsed),
                ByteFormatter.Speed(speed),
                $"Files: {destFiles}/{sourceFiles}",
                matchText
            }.Where(s => !string.IsNullOrWhiteSpace(s)))
        };
    }

    private static int StatusRank(JobStatus status) =>
        status switch
        {
            JobStatus.Failed => 5,
            JobStatus.Incomplete => 4,
            JobStatus.Cancelled => 3,
            JobStatus.Copying or JobStatus.Preparing or JobStatus.Enumerating or JobStatus.Verifying
                or JobStatus.Paused or JobStatus.PausedOutsideHours => 2,
            JobStatus.Completed => 1,
            _ => 0
        };

    public static void MarkStarted(Job job)
    {
        job.StartedUtc = DateTimeOffset.UtcNow;
        job.EndedUtc = null;
        job.SourceFiles = 0;
        job.DestFiles = 0;
        job.SourceFolders = 0;
        job.DestFolders = 0;
        job.BytesCopied = 0;
        job.AverageBytesPerSecond = 0;
    }

    public static void MarkResumed(Job job)
    {
        job.EndedUtc = null;
        job.StartedUtc ??= DateTimeOffset.UtcNow;
    }

    public static string FormatLocal(DateTimeOffset utc) =>
        utc.ToLocalTime().ToString("d MMM yyyy HH:mm:ss", CultureInfo.CurrentCulture);

    private static string FormatPair(int source, int dest) =>
        string.Create(CultureInfo.InvariantCulture, $"Source {source}  Dest {dest}");

    private static FileTotals SafeTotals(JobJournal journal)
    {
        try
        {
            return journal.Totals();
        }
        catch
        {
            return default;
        }
    }
}
