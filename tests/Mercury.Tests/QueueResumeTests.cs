namespace Mercury.Tests;

public class QueueResumeTests
{
    [Fact]
    public async Task ResumeOrRetry_IncompleteJob_StartsCopyEvenIfGlobalPauseStuck()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-qresume-sched-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            var engine = new SignalEngine();
            using var scheduler = new JobScheduler(paths, engine, new SkipRundown());
            var src = Path.Combine(root, "src");
            var dest = Path.Combine(root, "dest");
            Directory.CreateDirectory(src);
            Directory.CreateDirectory(dest);
            File.WriteAllText(Path.Combine(src, "a.txt"), "a");
            var job = new Job
            {
                Name = "anchor",
                SourcePath = src,
                DestinationPath = dest,
                Status = JobStatus.Pending
            };
            scheduler.Enqueue(job);
            job.Status = JobStatus.Incomplete;
            scheduler.PauseAll();
            Assert.True(scheduler.GlobalPause.IsPaused);

            scheduler.ResumeOrRetry(job.Id);

            await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(scheduler.GlobalPause.IsPaused);
            Assert.Contains(job.Id, scheduler.RunningJobIds);
            engine.Release.TrySetResult();
            await WaitUntil(() => !scheduler.RunningJobIds.Contains(job.Id), TimeSpan.FromSeconds(10));
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
                // leftover
            }
        }
    }

    [Fact]
    public async Task ResumeOrRetry_PendingRow_ForceStartsThatJob()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-qresume-force-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            var engine = new SignalEngine();
            using var scheduler = new JobScheduler(paths, engine, new SkipRundown());
            var src = Path.Combine(root, "src");
            var dest = Path.Combine(root, "dest");
            Directory.CreateDirectory(src);
            Directory.CreateDirectory(dest);
            File.WriteAllText(Path.Combine(src, "a.txt"), "a");
            var later = new Job
            {
                Name = "later",
                SourcePath = src,
                DestinationPath = dest,
                Status = JobStatus.Pending,
                ScheduledStart = DateTimeOffset.Now.AddHours(5)
            };
            var now = new Job
            {
                Name = "now",
                SourcePath = src,
                DestinationPath = dest,
                Status = JobStatus.Pending
            };
            scheduler.Enqueue(later);
            scheduler.Enqueue(now);

            scheduler.ResumeOrRetry(now.Id);

            await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("now", engine.StartedName);
            engine.Release.TrySetResult();
            await WaitUntil(() => !scheduler.HasRunningJob, TimeSpan.FromSeconds(10));
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
                // leftover
            }
        }
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("condition not met");
    }

    private sealed class SignalEngine : ICopyEngine
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? StartedName { get; private set; }

        public async Task RunAsync(
            Job job,
            JobJournal journal,
            BandwidthBudget budget,
            PauseGate pause,
            IJobLog log,
            IProgress<JobProgress>? progress,
            CancellationToken cancellationToken)
        {
            StartedName = job.Name;
            job.Status = JobStatus.Copying;
            progress?.Report(new JobProgress
            {
                JobId = job.Id,
                JobName = job.Name,
                Status = JobStatus.Copying,
                Message = "Copying…"
            });
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            job.Status = JobStatus.Completed;
            job.ResultMessage = "Verified complete.";
        }
    }

    private sealed class SkipRundown : IRundownCapture
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
