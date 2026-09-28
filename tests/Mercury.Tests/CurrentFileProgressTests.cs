namespace Mercury.Tests;

public class CurrentFileProgressTests
{
    [Fact]
    public void JobProgressPercentIsFileFractionWhenSizeKnown()
    {
        var progress = new JobProgress
        {
            CurrentFileBytesCopied = 250,
            CurrentFileBytesTotal = 1000
        };
        Assert.Equal(25, progress.CurrentFilePercent);
        Assert.Equal(0, new JobProgress().CurrentFilePercent);
    }

    [Fact]
    public void EstimateFileEtaIsPlaceholderUntilRateKnown()
    {
        var now = new DateTimeOffset(2026, 9, 29, 8, 0, 1, TimeSpan.Zero);
        Assert.Null(ProgressHeader.EstimateFileEta(100, 1000, now.AddSeconds(-1), now));
        Assert.Null(ProgressHeader.EstimateFileEta(0, 1000, now.AddSeconds(-10), now));
        Assert.Null(ProgressHeader.EstimateFileEta(100, 0, now.AddSeconds(-10), now));
        Assert.Null(ProgressHeader.EstimateFileEta(100, 1000, null, now));
    }

    [Fact]
    public void EstimateFileEtaUsesRemainingOverFileRate()
    {
        var now = new DateTimeOffset(2026, 9, 29, 8, 0, 10, TimeSpan.Zero);
        var started = now.AddSeconds(-10);
        var eta = ProgressHeader.EstimateFileEta(5_000_000, 10_000_000, started, now);
        Assert.NotNull(eta);
        Assert.Equal(10, eta!.Value.TotalSeconds, 3);
    }

    [Fact]
    public void PauseGateExposesStartedTimeUntilEndFile()
    {
        var gate = new PauseGate();
        Assert.Null(gate.CurrentFileStartedUtc);
        gate.BeginFile("clip.mkv", 1000);
        var started = gate.CurrentFileStartedUtc;
        Assert.NotNull(started);
        gate.AddFileBytes(250);
        Assert.Equal(250, gate.CurrentFileCopied);
        Assert.Equal(1000, gate.CurrentFileSize);
        Assert.Equal(started, gate.CurrentFileStartedUtc);
        gate.EndFile();
        Assert.Null(gate.CurrentFileStartedUtc);
        Assert.Equal(0, gate.CurrentFileCopied);
        Assert.Null(gate.CurrentFilePath);
    }

    [Fact]
    public void ReporterSnapshotIncludesCurrentFileFraction()
    {
        var job = new Job
        {
            Name = "t",
            SourcePath = @"D:\src",
            DestinationPath = @"D:\dest",
            Options = new JobOptions(),
            Status = JobStatus.Copying,
            StartedUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        var pause = new PauseGate();
        JobProgress? got = null;
        var stages = CopyPipeline.For(job, hasJournalFiles: true, pack: false);
        var reporter = new JobProgressReporter(
            job, "t", cloud: false, stages, new SyncProgress(p => got = p), log: null, pause);
        reporter.Enter(CopyStageKind.Transferring, JobStatus.Copying, "Copying…");
        pause.BeginFile(@"day1\clip.mkv", 1000);
        pause.AddFileBytes(250);
        reporter.Pulse();

        Assert.NotNull(got);
        Assert.Equal(250, got!.CurrentFileBytesCopied);
        Assert.Equal(1000, got.CurrentFileBytesTotal);
        Assert.Equal(25, got.CurrentFilePercent);
        Assert.NotNull(got.CurrentFileStartedUtc);
        Assert.Contains("clip.mkv", got.CurrentFile, StringComparison.OrdinalIgnoreCase);

        pause.EndFile();
        reporter.Pulse();
        Assert.Equal(0, got.CurrentFileBytesCopied);
        Assert.Equal(0, got.CurrentFileBytesTotal);
        Assert.Null(got.CurrentFileStartedUtc);
        Assert.True(string.IsNullOrEmpty(got.CurrentFile));
    }

    [Fact]
    public void ProgressStatsFileEtaAfterWarmupMatchesRemainingOverRate()
    {
        var now = new DateTimeOffset(2026, 9, 29, 8, 0, 10, TimeSpan.Zero);
        var stats = ProgressStats.From(new JobProgress
        {
            Status = JobStatus.Copying,
            CurrentFile = "big.bin",
            CurrentFileBytesCopied = 5_000_000,
            CurrentFileBytesTotal = 10_000_000,
            CurrentFileStartedUtc = now.AddSeconds(-10)
        }, now);

        Assert.Equal(50, stats.CurrentFilePercent);
        Assert.Equal(ByteFormatter.Eta(TimeSpan.FromSeconds(10)), stats.FileEta.Value);
    }

    private sealed class SyncProgress(Action<JobProgress> onReport) : IProgress<JobProgress>
    {
        public void Report(JobProgress value) => onReport(value);
    }
}
