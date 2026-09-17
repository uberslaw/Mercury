namespace Mercury.Tests;

public class ResumeAndVerifyTests
{
    [Fact]
    public async Task CopyThenVerifyComplete()
    {
        var (src, dest, paths) = CreateTrees();
        try
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "alpha");
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            File.WriteAllText(Path.Combine(src, "sub", "b.bin"), new string('x', 10_000));

            var job = NewJob(src, dest, paths);
            await RunAsync(job, paths, resume: false);

            Assert.True(job.Status == JobStatus.Completed, $"status={job.Status} msg={job.ResultMessage}");
            Assert.True(File.Exists(Path.Combine(dest, Path.GetFileName(src), "a.txt")));
            Assert.True(File.Exists(Path.Combine(dest, Path.GetFileName(src), "sub", "b.bin")));
            Assert.NotNull(job.StartedUtc);
            Assert.NotNull(job.EndedUtc);
            Assert.Equal(2, job.SourceFiles);
            Assert.Equal(2, job.DestFiles);
            Assert.Equal(1, job.SourceFolders);
            Assert.Equal(1, job.DestFolders);
            Assert.True(job.BytesCopied > 0);
        }
        finally
        {
            Cleanup(src, dest, paths);
        }
    }

    [Fact]
    public async Task CopyReportsPipelineStages()
    {
        var (src, dest, paths) = CreateTrees();
        try
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "alpha");
            var job = NewJob(src, dest, paths);
            var seen = new HashSet<string>();
            var scheduler = new JobScheduler(paths);
            scheduler.ProgressChanged += (_, p) =>
            {
                if (!string.IsNullOrWhiteSpace(p.StageName))
                {
                    seen.Add(p.StageName);
                }
            };
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await scheduler.StartAsync(job, resumeJournal: false, cts.Token);
            scheduler.Dispose();

            Assert.Contains("Preparing destination", seen);
            Assert.Contains("Enumerating source", seen);
            Assert.Contains("Checking destination space", seen);
            Assert.Contains("Copying", seen);
            Assert.Contains("Verifying", seen);
            Assert.Contains("Writing rundown", seen);
        }
        finally
        {
            Cleanup(src, dest, paths);
        }
    }

    [Fact]
    public async Task ResumeSkipsAlreadyCopiedFiles()
    {
        var (src, dest, paths) = CreateTrees();
        try
        {
            File.WriteAllText(Path.Combine(src, "keep.txt"), "one");
            File.WriteAllText(Path.Combine(src, "more.txt"), "two");

            var job = NewJob(src, dest, paths);
            await RunAsync(job, paths, resume: false);
            Assert.Equal(JobStatus.Completed, job.Status);

            var destFile = Path.Combine(dest, Path.GetFileName(src), "more.txt");
            var stamp = File.GetLastWriteTimeUtc(destFile);

            job.Status = JobStatus.Pending;
            job.Options.Overwrite = OverwritePolicy.SkipIfNewerOrEqual;
            await Task.Delay(200);
            await RunAsync(job, paths, resume: true);

            Assert.Equal(stamp, File.GetLastWriteTimeUtc(destFile));
            Assert.Equal("two", File.ReadAllText(destFile));
        }
        finally
        {
            Cleanup(src, dest, paths);
        }
    }

    [Fact]
    public async Task VerifyFailsWhenDestFileMissing()
    {
        var (src, dest, paths) = CreateTrees();
        try
        {
            File.WriteAllText(Path.Combine(src, "gone.txt"), "data");
            var job = NewJob(src, dest, paths);
            await RunAsync(job, paths, resume: false);
            Assert.Equal(JobStatus.Completed, job.Status);

            var copied = Path.Combine(dest, Path.GetFileName(src), "gone.txt");
            File.Delete(copied);

            job.Status = JobStatus.Pending;
            await RunAsync(job, paths, resume: true);

            Assert.Equal(JobStatus.Incomplete, job.Status);
            Assert.True(job.IssueCount > 0);
        }
        finally
        {
            Cleanup(src, dest, paths);
        }
    }

    private static (string src, string dest, AppPaths paths) CreateTrees()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-test-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(root, "src");
        var dest = Path.Combine(root, "dest");
        var data = Path.Combine(root, "app");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);
        Directory.CreateDirectory(data);
        return (src, dest, new AppPaths(data));
    }

    private static Job NewJob(string src, string dest, AppPaths paths) =>
        new()
        {
            Name = "test",
            SourcePath = src,
            DestinationPath = dest,
            SourceKind = SourceKind.Folder,
            Options = new JobOptions { RetryCount = 1, RetryWaitSeconds = 1 }
        };

    private static async Task RunAsync(Job job, AppPaths paths, bool resume)
    {
        var scheduler = new JobScheduler(paths);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await scheduler.StartAsync(job, resume, cts.Token);
        var stored = scheduler.TryLoadLastJob();
        if (stored is not null)
        {
            job.Status = stored.Status;
            job.IssueCount = stored.IssueCount;
            job.ResultMessage = stored.ResultMessage;
            job.StartedUtc = stored.StartedUtc;
            job.EndedUtc = stored.EndedUtc;
            job.SourceFiles = stored.SourceFiles;
            job.DestFiles = stored.DestFiles;
            job.SourceFolders = stored.SourceFolders;
            job.DestFolders = stored.DestFolders;
            job.BytesCopied = stored.BytesCopied;
            job.AverageBytesPerSecond = stored.AverageBytesPerSecond;
        }
        else if (string.IsNullOrEmpty(job.ResultMessage))
        {
            job.ResultMessage = "journal missing after run";
        }

        scheduler.Dispose();
    }

    private static void Cleanup(string src, string dest, AppPaths paths)
    {
        try
        {
            var root = Directory.GetParent(src)?.FullName;
            if (root is not null && Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
        catch
        {
            // temp leftover is OK
        }
    }
}
