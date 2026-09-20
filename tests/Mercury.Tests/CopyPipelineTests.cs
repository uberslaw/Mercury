namespace Mercury.Tests;

public class CopyPipelineTests
{
    [Fact]
    public void FreshCopyHasPrepareEnumerateSpaceCopyVerifyRundown()
    {
        var job = new Job();
        var stages = CopyPipeline.For(job, hasJournalFiles: false, pack: false);
        Assert.Equal(
            [
                "Preparing destination",
                "Enumerating source",
                "Checking destination space",
                "Copying",
                "Verifying",
                "Writing rundown"
            ],
            stages.Select(s => s.Label));
        Assert.Equal("Stage: 2 of 6 — Enumerating source", CopyPipeline.Format(2, stages.Count, "Enumerating source"));
    }

    [Fact]
    public void PackUsesPackingLabelInsteadOfCopying()
    {
        var stages = CopyPipeline.For(new Job(), hasJournalFiles: false, pack: true);
        Assert.Contains(stages, s => s.Kind == CopyStageKind.Transferring && s.Label == "Packing");
        Assert.Contains(stages, s => s.Kind == CopyStageKind.Unpacking && s.Label == "Unpacking");
        Assert.DoesNotContain(stages, s => s.Label == "Copying");
    }

    [Fact]
    public void DryRunSkipsCopyAndVerify()
    {
        var job = new Job { Options = new JobOptions { DryRun = true } };
        var stages = CopyPipeline.For(job, hasJournalFiles: false, pack: false);
        Assert.Equal(
            [
                "Preparing destination",
                "Enumerating source",
                "Checking destination space",
                "Writing rundown"
            ],
            stages.Select(s => s.Label));
    }

    [Fact]
    public void CatcherAddsHttpsPushStageAndSkipsLocalSpaceCheck()
    {
        var job = new Job
        {
            Catcher = new CatcherTarget { TemplateName = "Warehouse" }
        };
        var stages = CopyPipeline.For(job, hasJournalFiles: false, pack: true);
        Assert.Equal(
            [
                "Preparing Catcher send",
                "Enumerating source",
                "Packing",
                "Sending over HTTPS",
                "Verifying",
                "Writing rundown"
            ],
            stages.Select(s => s.Label));
        Assert.DoesNotContain(stages, s => s.Kind == CopyStageKind.CheckingDestinationSpace);
        Assert.DoesNotContain(stages, s => s.Kind == CopyStageKind.Unpacking);
    }

    [Fact]
    public void ResumePackIncludesUnpacking()
    {
        var stages = CopyPipeline.For(new Job(), hasJournalFiles: true, pack: true);
        Assert.Equal(
            [
                "Preparing destination",
                "Packing",
                "Unpacking",
                "Verifying",
                "Writing rundown"
            ],
            stages.Select(s => s.Label));
    }

    [Fact]
    public void ResumeSkipsEnumerateAndSpaceCheck()
    {
        var stages = CopyPipeline.For(new Job(), hasJournalFiles: true, pack: false);
        Assert.Equal(
            [
                "Preparing destination",
                "Copying",
                "Verifying",
                "Writing rundown"
            ],
            stages.Select(s => s.Label));
    }

    [Fact]
    public void ResumeWithSourceScanAddsCheckingStage()
    {
        var job = new Job { ScanSourceOnResume = true };
        var stages = CopyPipeline.For(job, hasJournalFiles: true, pack: false);
        Assert.Contains(stages, s => s.Label == "Checking source for changes");
        Assert.DoesNotContain(stages, s => s.Label == "Enumerating source");
    }
}

public class HistoryStoreTests
{
    [Fact]
    public void RoundtripsNewestFirstAndRestoresRundown()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            var older = Finished("old", new DateTimeOffset(2026, 9, 16, 3, 0, 0, TimeSpan.Zero));
            var newer = Finished("new", new DateTimeOffset(2026, 9, 16, 4, 0, 0, TimeSpan.Zero));
            HistoryStore.Record(paths, older);
            HistoryStore.Record(paths, newer);

            var loaded = HistoryStore.Load(paths);
            Assert.Equal(2, loaded.Count);
            Assert.Equal("new", loaded[0].Name);
            Assert.Equal("old", loaded[1].Name);
            Assert.True(File.Exists(paths.HistoryFile));

            var rundown = TransferRundown.From(loaded[0].ToJob());
            Assert.True(rundown.IsVisible);
            Assert.Equal(RundownHighlight.Ok, rundown.Highlight);
            Assert.Contains("Verified", rundown.MatchText);
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
    public void SkipsInFlightJobs()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-history-skip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            HistoryStore.Record(paths, new Job
            {
                Name = "running",
                Status = JobStatus.Copying,
                StartedUtc = DateTimeOffset.UtcNow
            });
            Assert.Empty(HistoryStore.Load(paths));
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

    private static Job Finished(string name, DateTimeOffset started) =>
        new()
        {
            Name = name,
            SourcePath = @"D:\src",
            DestinationPath = @"E:\dst",
            Status = JobStatus.Completed,
            ResultMessage = "Verified complete.",
            StartedUtc = started,
            EndedUtc = started.AddMinutes(5),
            SourceFiles = 10,
            DestFiles = 10,
            SourceFolders = 1,
            DestFolders = 1,
            BytesCopied = 4096,
            AverageBytesPerSecond = 100
        };
}
