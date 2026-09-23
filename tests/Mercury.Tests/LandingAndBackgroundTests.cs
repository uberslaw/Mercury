using System.Diagnostics;

namespace Mercury.Tests;

public class LandingPathTests
{
    [Fact]
    public void PackedZipLivesUnderSourceLandingNotDestCompressedSibling()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-land-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(root, "Anchor Span");
        var dest = Path.Combine(root, "EngA data drive");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(src, "a.txt"), "x");
        try
        {
            var mapping = CopyShape.Resolve(src, dest, includeSourceFolderName: true);
            var zip = ZipPack.ZipPath(mapping);
            var landing = Path.Combine(dest, "Anchor Span");
            Assert.Equal(landing, mapping.DestRoot, ignoreCase: true);
            Assert.Equal(Path.Combine(landing, ZipPack.TransportFolder, "Anchor Span-transport.zip"), zip, ignoreCase: true);
            Assert.StartsWith(landing, zip, StringComparison.OrdinalIgnoreCase);
            Assert.False(
                zip.StartsWith(Path.Combine(dest, ZipPack.TransportFolder), StringComparison.OrdinalIgnoreCase),
                zip);
            Assert.False(string.Equals(Path.Combine(dest, "Anchor Span.zip"), zip, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void PackedZipWithIncludeOffLivesUnderDestCompressed()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-land-off-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(root, "Anchor Span");
        var dest = Path.Combine(root, "EngA");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(src, "a.txt"), "x");
        try
        {
            var mapping = CopyShape.Resolve(src, dest, includeSourceFolderName: false);
            Assert.Equal(dest, mapping.DestRoot, ignoreCase: true);
            var zip = ZipPack.ZipPath(mapping);
            Assert.Equal(Path.Combine(dest, ZipPack.TransportFolder, Path.GetFileName(dest) + "-transport.zip"), zip, ignoreCase: true);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void TwoSourcesPackZipsUnderEachLanding()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-land-2-" + Guid.NewGuid().ToString("N"));
        var one = Path.Combine(root, "Anchor Span");
        var two = Path.Combine(root, "Warren Truss");
        var dest = Path.Combine(root, "EngA");
        Directory.CreateDirectory(one);
        Directory.CreateDirectory(two);
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(one, "a.txt"), "a");
        File.WriteAllText(Path.Combine(two, "b.txt"), "b");
        try
        {
            var mappings = CopyShape.ResolveAll([one, two], dest, includeSourceFolderName: true);
            Assert.Equal(2, mappings.Count);
            Assert.Equal(
                Path.Combine(dest, "Anchor Span", ZipPack.TransportFolder, "Anchor Span-transport.zip"),
                ZipPack.ZipPath(mappings[0]),
                ignoreCase: true);
            Assert.Equal(
                Path.Combine(dest, "Warren Truss", ZipPack.TransportFolder, "Warren Truss-transport.zip"),
                ZipPack.ZipPath(mappings[1]),
                ignoreCase: true);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void ProgressRelativePrefixesLandingFolder()
    {
        var job = new Job
        {
            SourcePath = @"D:\Anchor Span",
            DestinationPath = @"Z:\EngA data drive",
            Options = new JobOptions { IncludeSourceFolderName = true }
        };
        Assert.Equal(
            Path.Combine("Anchor Span", "compressed", "a.zip"),
            CopyShape.ProgressRelative(job, Path.Combine("compressed", "a.zip")));
        Assert.Equal(
            Path.Combine("Anchor Span", "compressed", "a.zip"),
            CopyShape.ProgressRelative(job, Path.Combine(@"Z:\EngA data drive", "Anchor Span", "compressed", "a.zip")));
    }

    [Fact]
    public void ExtractRootFromCompressedTransportIsLanding()
    {
        var zip = Path.Combine(@"Z:\EngA", "Anchor Span", ZipPack.TransportFolder, "Anchor Span-transport.zip");
        Assert.Equal(Path.Combine(@"Z:\EngA", "Anchor Span"), ZipPack.ExtractRootFromZip(zip), ignoreCase: true);
    }

    private static void TryDelete(string root)
    {
        try { Directory.Delete(root, true); } catch { /* leftover */ }
    }
}

public class BackgroundVerifyTests
{
    [Fact]
    public async Task CopySlotFreesWhileVerifyRunsAndNextCopyStarts()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-bg-vfy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var engine = new CopyThenGateVerify(entered, hold);
            using var scheduler = new JobScheduler(paths, engine, new ImmediateRundown());
            var first = NewJob(root, "one");
            var second = NewJob(root, "two");
            scheduler.Enqueue(first, startNow: true);
            scheduler.Enqueue(second);

            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntil(() => scheduler.RunningJobIds.Contains(second.Id), TimeSpan.FromSeconds(10));

            Assert.Contains(first.Id, scheduler.RundownJobIds);
            Assert.Equal(second.Id, Assert.Single(scheduler.RunningJobIds));
            Assert.True(engine.MaxCopyConcurrent <= 1);
            Assert.True(scheduler.BlocksStart);

            engine.ReleaseSecond.TrySetResult();
            hold.TrySetResult();
            await WaitUntil(() => !scheduler.RunningJobIds.Contains(second.Id), TimeSpan.FromSeconds(10));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* leftover */ }
        }
    }

    [Fact]
    public void VerifyStatsShowDashEtaUntilRateExists()
    {
        var stats = ProgressStats.From(new JobProgress
        {
            Status = JobStatus.Verifying,
            Message = "Verifying…",
            StageName = "Verifying",
            StageIndex = 3,
            StageCount = 4,
            RundownDone = 0,
            RundownTotal = 26013,
            FilesCopied = 0,
            FilesTotal = 26013,
            StartedUtc = DateTimeOffset.UtcNow,
            StageStartedUtc = DateTimeOffset.UtcNow
        });

        Assert.Equal("—", stats.Eta.Value);
        Assert.Contains("Verifying", stats.Stage.Value, StringComparison.Ordinal);
    }

    private static Job NewJob(string root, string name)
    {
        var src = Path.Combine(root, name + "-src");
        var dest = Path.Combine(root, name + "-dest");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(src, "a.txt"), "a");
        return new Job
        {
            Name = name,
            SourcePath = src,
            DestinationPath = dest,
            Status = JobStatus.Pending
        };
    }

    private static async Task WaitUntil(Func<bool> ready, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (ready())
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("condition not met");
    }

    private sealed class CopyThenGateVerify : ICopyEngine
    {
        private readonly TaskCompletionSource _entered;
        private readonly TaskCompletionSource _hold;
        private int _copyConcurrent;

        public CopyThenGateVerify(TaskCompletionSource entered, TaskCompletionSource hold)
        {
            _entered = entered;
            _hold = hold;
        }

        public int MaxCopyConcurrent { get; private set; }

        public TaskCompletionSource ReleaseSecond { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task RunAsync(
            Job job,
            JobJournal journal,
            BandwidthBudget budget,
            PauseGate pause,
            IJobLog log,
            IProgress<JobProgress>? progress,
            CancellationToken cancellationToken)
        {
            var n = Interlocked.Increment(ref _copyConcurrent);
            if (n > MaxCopyConcurrent)
            {
                MaxCopyConcurrent = n;
            }

            try
            {
                job.Status = JobStatus.Copying;
                job.BytesCopied = 1;
                progress?.Report(new JobProgress
                {
                    JobId = job.Id,
                    JobName = job.Name,
                    Status = JobStatus.Copying,
                    StageName = "Copying",
                    StageIndex = 2,
                    StageCount = 4,
                    BytesCopied = 1,
                    BytesTotal = 1,
                    FilesCopied = 1,
                    FilesTotal = 1,
                    StartedUtc = job.StartedUtc
                });
                if (job.Name == "two")
                {
                    await ReleaseSecond.Task.WaitAsync(cancellationToken);
                }
                else
                {
                    await Task.Delay(20, cancellationToken);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _copyConcurrent);
            }
        }

        public void Verify(
            Job job,
            JobJournal journal,
            IJobLog log,
            IProgress<JobProgress>? progress,
            CancellationToken cancellationToken)
        {
            if (job.Name != "one")
            {
                job.Status = JobStatus.Completed;
                return;
            }

            job.Status = JobStatus.Verifying;
            progress?.Report(new JobProgress
            {
                JobId = job.Id,
                JobName = job.Name,
                Status = JobStatus.Verifying,
                StageName = "Verifying",
                Message = "Verifying…",
                RundownDone = 1,
                RundownTotal = 10,
                FilesCopied = 1,
                FilesTotal = 10,
                StartedUtc = job.StartedUtc
            });
            _entered.TrySetResult();
            _hold.Task.Wait(cancellationToken);
            job.Status = JobStatus.Completed;
            job.ResultMessage = "Verified complete.";
        }
    }

    private sealed class ImmediateRundown : IRundownCapture
    {
        public void Capture(
            Job job,
            JobJournal journal,
            CopyMapping? mapping,
            IJobLog? log,
            string name,
            CancellationToken cancellationToken,
            Action<RundownProgress>? progress)
        {
        }
    }
}
