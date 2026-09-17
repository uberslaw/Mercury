namespace Mercury.Tests;

public class FreeSpaceTests
{
    [Fact]
    public void ShortageMessageMatchesProductWording()
    {
        var needed = (long)(4.53 * 1024L * 1024 * 1024 * 1024);
        var available = (long)(2.90 * 1024L * 1024 * 1024 * 1024);
        var message = FreeSpace.ShortageMessage(needed, available);
        Assert.StartsWith("Not enough free space at destination. Need ", message);
        Assert.Contains("available", message);
        Assert.Contains("TB", message);
    }

    [Fact]
    public void CheckOrWarnThrowsWhenShortAndGuardOn()
    {
        var job = new Job { Id = "job1", Name = "copy", Options = new JobOptions() };
        var log = new MemoryLog();
        var ex = Assert.Throws<IOException>(() =>
            FreeSpace.CheckOrWarn(job, needed: 1000, available: 100, log, "copy"));
        Assert.Equal(FreeSpace.ShortageMessage(1000, 100), ex.Message);
        Assert.Empty(log.Lines);
    }

    [Fact]
    public void CheckOrWarnContinuesAndLogsWhenDestinationCanExpand()
    {
        var job = new Job
        {
            Id = "job1",
            Name = "copy",
            Options = new JobOptions { IgnoreFreeSpaceCheck = true }
        };
        var log = new MemoryLog();
        FreeSpace.CheckOrWarn(job, needed: 1000, available: 100, log, "copy");
        Assert.Single(log.Lines);
        Assert.Equal("Info", log.Lines[0].Level);
        Assert.StartsWith("Warning: Not enough free space at destination.", log.Lines[0].Message);
        Assert.Contains("Continuing because destination can expand.", log.Lines[0].Message);
    }

    [Fact]
    public void CheckOrWarnSkipsWhenEnoughSpaceOrUnknown()
    {
        var job = new Job { Id = "job1", Name = "copy" };
        var log = new MemoryLog();
        FreeSpace.CheckOrWarn(job, needed: 100, available: 1000, log, "copy");
        FreeSpace.CheckOrWarn(job, needed: 1000, available: null, log, "copy");
        Assert.Empty(log.Lines);
    }

    [Fact]
    public void CheckOrWarnSkipsDryRunEvenWhenShort()
    {
        var job = new Job
        {
            Id = "job1",
            Name = "copy",
            Options = new JobOptions { DryRun = true }
        };
        var log = new MemoryLog();
        FreeSpace.CheckOrWarn(job, needed: 1000, available: 100, log, "copy");
        Assert.Empty(log.Lines);
    }

    [Fact]
    public async Task FailedJobClearsRunningBeforeFinalProgress()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-space-fail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            using var scheduler = new JobScheduler(paths, new ThrowingEngine());
            var idleOnFailedProgress = false;
            scheduler.ProgressChanged += (_, p) =>
            {
                if (p.Status == JobStatus.Failed)
                {
                    idleOnFailedProgress = !scheduler.HasRunningJob;
                }
            };

            var job = new Job
            {
                Name = "fail",
                SourcePath = root,
                DestinationPath = Path.Combine(root, "dest")
            };
            Directory.CreateDirectory(job.DestinationPath);
            await scheduler.StartAsync(job, resumeJournal: false, CancellationToken.None);

            Assert.Equal(JobStatus.Failed, job.Status);
            Assert.Contains("Not enough free space", job.ResultMessage);
            Assert.False(scheduler.HasRunningJob);
            Assert.True(idleOnFailedProgress);
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

    private sealed class ThrowingEngine : ICopyEngine
    {
        public Task RunAsync(
            Job job,
            JobJournal journal,
            BandwidthBudget budget,
            PauseGate pause,
            IJobLog log,
            IProgress<JobProgress>? progress,
            CancellationToken cancellationToken) =>
            throw new IOException(FreeSpace.ShortageMessage(1000, 100));
    }
}
