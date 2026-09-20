using System.Diagnostics;

namespace Mercury.Tests;

public class BackgroundRundownTests
{
    [Fact]
    public async Task CopySlotFreesWhileRundownRuns()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-bg-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var scheduler = new JobScheduler(paths, new CompleteEngine(), new GateRundown(entered, hold));
            var job = NewJob(root, "one");
            var start = scheduler.StartAsync(job, resumeJournal: false, CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.False(scheduler.BlocksStart);
            Assert.True(scheduler.HasBackgroundRundown);
            Assert.Empty(scheduler.RunningJobIds);
            Assert.Contains(job.Id, scheduler.RundownJobIds);

            var sw = Stopwatch.StartNew();
            scheduler.Stop(job.Id);
            await start.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"stop took {sw.Elapsed}");
            Assert.False(scheduler.HasBackgroundRundown);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task NextCopyStartsWhilePreviousRundownRuns()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-bg-q-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var engine = new CompleteEngine();
            using var scheduler = new JobScheduler(paths, engine, new GateRundown(entered, hold));
            var first = NewJob(root, "one");
            var second = NewJob(root, "two");
            scheduler.Enqueue(first, startNow: true);
            scheduler.Enqueue(second);

            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntil(() => scheduler.RunningJobIds.Contains(second.Id), TimeSpan.FromSeconds(10));

            Assert.Contains(first.Id, scheduler.RundownJobIds);
            Assert.Equal(second.Id, Assert.Single(scheduler.RunningJobIds));
            Assert.True(engine.MaxConcurrent <= 1);
            Assert.True(scheduler.BlocksStart);

            engine.ReleaseSecond.TrySetResult();
            hold.TrySetResult();
            await WaitUntil(() => !scheduler.RunningJobIds.Contains(second.Id), TimeSpan.FromSeconds(10));
        }
        finally
        {
            TryDelete(root);
        }
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

    private static void TryDelete(string root)
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

    private sealed class CompleteEngine : ICopyEngine
    {
        private int _concurrent;

        public int MaxConcurrent { get; private set; }

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
            var n = Interlocked.Increment(ref _concurrent);
            if (n > MaxConcurrent)
            {
                MaxConcurrent = n;
            }

            try
            {
                if (job.Name == "two")
                {
                    await ReleaseSecond.Task.WaitAsync(cancellationToken);
                }
                else
                {
                    await Task.Delay(20, cancellationToken);
                }

                job.Status = JobStatus.Completed;
                job.ResultMessage = "Verified complete.";
                progress?.Report(new JobProgress
                {
                    JobId = job.Id,
                    JobName = job.Name,
                    Status = JobStatus.Completed,
                    StageIndex = 5,
                    StageCount = 6,
                    StageName = "Verifying",
                    Message = job.ResultMessage,
                    StartedUtc = job.StartedUtc
                });
            }
            finally
            {
                Interlocked.Decrement(ref _concurrent);
            }
        }
    }

    private sealed class GateRundown : IRundownCapture
    {
        private readonly TaskCompletionSource _entered;
        private readonly TaskCompletionSource _hold;

        public GateRundown(TaskCompletionSource entered, TaskCompletionSource hold)
        {
            _entered = entered;
            _hold = hold;
        }

        public void Capture(
            Job job,
            JobJournal journal,
            CopyMapping? mapping,
            IJobLog? log,
            string name,
            CancellationToken cancellationToken,
            Action<RundownProgress>? progress)
        {
            _entered.TrySetResult();
            progress?.Invoke(new RundownProgress(1, 10, 2, TimeSpan.FromSeconds(5), "destination"));
            _hold.Task.Wait(cancellationToken);
        }
    }
}
