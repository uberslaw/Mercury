namespace Mercury.Tests;

public class TransferRundownTests
{
    [Fact]
    public void DurationFormatsHoursMinutesSeconds()
    {
        Assert.Equal("1h 12m 05s", ByteFormatter.Duration(new TimeSpan(1, 12, 5)));
        Assert.Equal("3m 07s", ByteFormatter.Duration(new TimeSpan(0, 3, 7)));
        Assert.Equal("9s", ByteFormatter.Duration(TimeSpan.FromSeconds(9)));
        Assert.Equal("26h 00m 00s", ByteFormatter.Duration(TimeSpan.FromHours(26)));
    }

    [Fact]
    public void AverageUsesCopiedPlusSkippedOverWallClock()
    {
        Assert.Equal(5000, TransferRundown.AverageBytesPerSecond(10_000, TimeSpan.FromSeconds(2)));
        Assert.Equal(0, TransferRundown.AverageBytesPerSecond(10_000, TimeSpan.Zero));
        Assert.Equal(0, TransferRundown.AverageBytesPerSecond(-5, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void EmptyRundownCapitalizesSourceDest()
    {
        Assert.Equal("Source 0  Dest 0", TransferRundown.Empty.FilesText);
        Assert.Equal("Source 0  Dest 0", TransferRundown.Empty.FoldersText);
    }

    [Fact]
    public void LiveRundownShowsStartedAndElapsedWithoutEnded()
    {
        var job = new Job
        {
            Status = JobStatus.Enumerating,
            StartedUtc = DateTimeOffset.UtcNow.AddSeconds(-12),
            SourceFiles = 40
        };

        var rundown = TransferRundown.From(job, DateTimeOffset.UtcNow);
        Assert.True(rundown.IsVisible);
        Assert.Equal("—", rundown.EndedText);
        Assert.False(string.IsNullOrWhiteSpace(rundown.ElapsedText));
        Assert.Contains("Source 40", rundown.FilesText);
        Assert.Equal("", rundown.MatchText);
    }

    [Fact]
    public void ProgressStatsFormatsLiveStageAndElapsed()
    {
        var now = new DateTimeOffset(2026, 9, 16, 10, 0, 12, TimeSpan.Zero);
        var stats = ProgressStats.From(new JobProgress
        {
            Status = JobStatus.Enumerating,
            StageIndex = 2,
            StageCount = 6,
            StageName = "Enumerating source",
            StartedUtc = now.AddSeconds(-12),
            StageStartedUtc = now.AddSeconds(-8)
        }, now);

        Assert.Equal("Stage: 2 of 6 — Enumerating source", stats.Stage.Display);
        Assert.False(stats.Job.HasValue);
        Assert.Equal("Elapsed", stats.Elapsed.Key);
        Assert.Contains("this stage", stats.ThisStage.Key, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProgressStatsIncludesCurrentFile()
    {
        var stats = ProgressStats.From(new JobProgress
        {
            Status = JobStatus.Copying,
            CurrentFile = @"day1\clip.mkv",
            FilesCopied = 3,
            FilesTotal = 10,
            BytesCopied = 100,
            BytesTotal = 1000
        });
        Assert.Equal(@"day1\clip.mkv", stats.File.Value);
        Assert.Contains(stats.TableCells, p => p.Key == "File");
    }

    [Fact]
    public void ProgressStatsShowsOverallFilesOnlyWhenQueueHasTwoJobs()
    {
        var current = new JobProgress { FilesCopied = 12, FilesTotal = 400 };
        var overall = new JobProgress { FilesCopied = 50, FilesTotal = 2000 };
        var one = ProgressStats.From(current, jobIndex: 1, jobCount: 1, overall: overall);
        Assert.Equal("12/400", one.Files.Value);
        Assert.False(one.Job.HasValue);

        var many = ProgressStats.From(current, jobIndex: 2, jobCount: 5, overall: overall);
        Assert.Equal("12/400", many.Files.Value);
        Assert.Equal("50/2000", many.OverallFiles.Value);
        Assert.Equal("2 of 5", many.Job.Value);
        Assert.Contains(many.TableCells, p => p.Key == "Job");
        Assert.Contains(many.TableCells, p => p.Key == "Overall files");
    }

    [Fact]
    public void RundownStatsUseFileRateAndAvoidDashEta()
    {
        var stats = ProgressStats.From(new JobProgress
        {
            Status = JobStatus.Completed,
            Message = CopyPipeline.RundownMessage,
            StageIndex = 6,
            StageCount = 6,
            StageName = CopyPipeline.RundownLabel,
            FilesCopied = 12591,
            FilesTotal = 12591,
            BytesCopied = 1000,
            BytesTotal = 1000,
            RundownDone = 4000,
            RundownTotal = 12591,
            RundownPerSecond = 80,
            Eta = TimeSpan.FromSeconds(107),
            StartedUtc = DateTimeOffset.UtcNow.AddMinutes(-8),
            StageStartedUtc = DateTimeOffset.UtcNow.AddSeconds(-20)
        });

        Assert.Equal("Stage: 6 of 6 — Writing rundown", stats.Stage.Display);
        Assert.Equal("80 files/s", stats.Speed.Value);
        Assert.DoesNotContain("—", stats.Eta.Value, StringComparison.Ordinal);
        Assert.InRange(new JobProgress
        {
            StageName = CopyPipeline.RundownLabel,
            RundownDone = 4000,
            RundownTotal = 12591
        }.Percent, 31, 33);
    }

    [Fact]
    public void RundownEtaShowsEllipsisUntilRateExists()
    {
        var stats = ProgressStats.From(new JobProgress
        {
            Status = JobStatus.Completed,
            Message = CopyPipeline.RundownMessage,
            StageName = CopyPipeline.RundownLabel,
            StageIndex = 6,
            StageCount = 6,
            RundownDone = 0,
            RundownTotal = 100,
            StartedUtc = DateTimeOffset.UtcNow
        });

        Assert.Equal("…", stats.Eta.Value);
        Assert.Equal("…", stats.Speed.Value);
    }

    [Fact]
    public void HelpUsesColonForStatsAndThemeExport()
    {
        var progress = HelpDocument.Sections.Single(s => s.Id == "progress");
        Assert.Contains("Files: 12/400", progress.Body, StringComparison.Ordinal);
        Assert.Contains("Job: 2 of 5", progress.Body, StringComparison.Ordinal);
        Assert.Contains("Job n of m is which queue item", progress.Body, StringComparison.Ordinal);
        Assert.Contains("Stage is that job", progress.Body, StringComparison.Ordinal);
        Assert.Contains("Rundown running in background", HelpDocument.Sections.Single(s => s.Id == "console").Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Files; ", progress.Body, StringComparison.Ordinal);

        var options = HelpDocument.Sections.Single(s => s.Id == "options");
        Assert.Contains("Throttle active PC", options.Body, StringComparison.OrdinalIgnoreCase);

        var queue = HelpDocument.Sections.Single(s => s.Id == "queue");
        Assert.Contains("On hold", queue.Body, StringComparison.OrdinalIgnoreCase);

        var theme = HelpDocument.Sections.Single(s => s.Id == "theme");
        Assert.Contains(".mercury-theme.json", theme.Body, StringComparison.Ordinal);
        Assert.Contains("fonts", theme.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HighlightIsOkWhenCompletedAndCountsMatch()
    {
        Assert.Equal(RundownHighlight.Ok, TransferRundown.Classify(JobStatus.Completed, countsMatch: true));
        var job = Finished(JobStatus.Completed, sourceFiles: 10, destFiles: 10, sourceFolders: 2, destFolders: 2);
        var rundown = TransferRundown.From(job);
        Assert.True(rundown.IsVisible);
        Assert.Equal(RundownHighlight.Ok, rundown.Highlight);
        Assert.Equal("Verified — Source and Dest match", rundown.MatchText);
        Assert.Contains("1h 12m 05s", rundown.ElapsedText);
        Assert.Contains(ByteFormatter.Speed(job.AverageBytesPerSecond), rundown.AverageSpeedText);
        Assert.Contains("Files:", rundown.OneLine, StringComparison.Ordinal);
        Assert.Contains("Started:", rundown.ConsoleLines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void HighlightIsDangerWhenFileCountsDiffer()
    {
        Assert.Equal(RundownHighlight.Danger, TransferRundown.Classify(JobStatus.Completed, countsMatch: false));
        var job = Finished(JobStatus.Completed, sourceFiles: 1250, destFiles: 1234, sourceFolders: 2, destFolders: 2);
        var rundown = TransferRundown.From(job);
        Assert.Equal(RundownHighlight.Danger, rundown.Highlight);
        Assert.Equal("Mismatch — Dest files 1234 vs Source 1250", rundown.MatchText);
    }

    [Fact]
    public void HighlightIsDangerWhenIncompleteOrVerifyFailed()
    {
        Assert.Equal(RundownHighlight.Danger, TransferRundown.Classify(JobStatus.Incomplete, countsMatch: true));
        var job = Finished(JobStatus.Incomplete, sourceFiles: 4, destFiles: 4, sourceFolders: 1, destFolders: 1);
        var rundown = TransferRundown.From(job);
        Assert.Equal(RundownHighlight.Danger, rundown.Highlight);
        Assert.Equal("Incomplete — verify failed", rundown.MatchText);
    }

    [Fact]
    public void HighlightIsWarnWhenCancelledAndCountsMatch()
    {
        Assert.Equal(RundownHighlight.Warn, TransferRundown.Classify(JobStatus.Cancelled, countsMatch: true));
        var job = Finished(JobStatus.Cancelled, sourceFiles: 3, destFiles: 3, sourceFolders: 1, destFolders: 1);
        Assert.Equal(RundownHighlight.Warn, TransferRundown.From(job).Highlight);
    }

    [Fact]
    public void QueueStoreRoundtripsRundownFields()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-rundown-q-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            var started = new DateTimeOffset(2026, 9, 16, 4, 2, 3, TimeSpan.Zero);
            var ended = started.AddMinutes(12);
            QueueStore.Save(paths,
            [
                new Job
                {
                    Name = "done",
                    Status = JobStatus.Completed,
                    StartedUtc = started,
                    EndedUtc = ended,
                    SourceFiles = 10,
                    DestFiles = 10,
                    SourceFolders = 2,
                    DestFolders = 2,
                    BytesCopied = 4096,
                    AverageBytesPerSecond = 100
                }
            ]);

            var loaded = QueueStore.Load(paths);
            Assert.Single(loaded);
            Assert.Equal(started, loaded[0].StartedUtc);
            Assert.Equal(ended, loaded[0].EndedUtc);
            Assert.Equal(10, loaded[0].SourceFiles);
            Assert.Equal(10, loaded[0].DestFiles);
            Assert.Equal(2, loaded[0].SourceFolders);
            Assert.Equal(2, loaded[0].DestFolders);
            Assert.Equal(4096, loaded[0].BytesCopied);
            Assert.Equal(100, loaded[0].AverageBytesPerSecond);
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
                // temp leftover is OK
            }
        }
    }

    [Fact]
    public void CaptureKeepsSourceFilesWhenJournalIsEmpty()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-rd-keep-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(root, "Anchor Span");
        var dest = Path.Combine(root, "EngA");
        Directory.CreateDirectory(Path.Combine(src, "compressed"));
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(src, "compressed", "a.txt"), "a");
        try
        {
            var job = new Job
            {
                SourcePath = src,
                DestinationPath = dest,
                StartedUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                SourceFiles = 26013,
                SourceFolders = 4,
                Options = new JobOptions { IncludeSourceFolderName = true }
            };
            using var journal = JobJournal.Create(Path.Combine(root, "job"), job);
            var mapping = CopyShape.Resolve(src, dest, includeSourceFolderName: true);
            TransferRundown.Capture(job, journal, mapping, log: null, name: "t");
            Assert.True(job.SourceFiles > 0);
            Assert.Equal(Path.Combine(dest, "Anchor Span"), mapping.DestRoot, ignoreCase: true);
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
                // temp leftover is OK
            }
        }
    }

    [Fact]
    public void NullOptionsDeserializeAsDefaultsWithWrapOn()
    {
        var job = new Job { Options = null! };
        Assert.True(job.Options.IncludeSourceFolderName);
        Assert.False(job.Options.DryRun);
    }

    [Fact]
    public void CountTreeExcludesSkippedSystemDirs()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-rundown-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            File.WriteAllText(Path.Combine(root, "a.txt"), "a");
            File.WriteAllText(Path.Combine(root, "sub", "b.txt"), "b");
            Directory.CreateDirectory(Path.Combine(root, "System Volume Information"));
            File.WriteAllText(Path.Combine(root, "System Volume Information", "hidden.txt"), "skip");
            File.WriteAllText(Path.Combine(root, "leftover.mercury.tmp"), "tmp");

            var counts = SourceWalker.CountTree(root, skipMercuryTemp: true);
            Assert.Equal(2, counts.Files);
            Assert.Equal(1, counts.Folders);
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
                // temp leftover is OK
            }
        }
    }

    private static Job Finished(
        JobStatus status,
        int sourceFiles,
        int destFiles,
        int sourceFolders,
        int destFolders) =>
        new()
        {
            Status = status,
            StartedUtc = new DateTimeOffset(2026, 9, 16, 3, 2, 3, TimeSpan.Zero),
            EndedUtc = new DateTimeOffset(2026, 9, 16, 4, 14, 8, TimeSpan.Zero),
            SourceFiles = sourceFiles,
            DestFiles = destFiles,
            SourceFolders = sourceFolders,
            DestFolders = destFolders,
            BytesCopied = 1024 * 1024,
            AverageBytesPerSecond = TransferRundown.AverageBytesPerSecond(
                1024 * 1024,
                TimeSpan.FromHours(1) + TimeSpan.FromMinutes(12) + TimeSpan.FromSeconds(5))
        };
}
