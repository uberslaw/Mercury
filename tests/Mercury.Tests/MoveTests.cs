namespace Mercury.Tests;

public class MoveTests
{
    [Fact]
    public void MoveDefaultsOffAndClones()
    {
        Assert.False(new JobOptions().Move);
        var copy = new JobOptions { Move = true }.Clone();
        Assert.True(copy.Move);
        Assert.Contains("Move", JobOptionBadges.For(new Job { Options = copy }));
        Assert.DoesNotContain("Move", JobOptionBadges.For(new Job()));
    }

    [Fact]
    public void QueueStoreRoundtripsMove()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-queue-move-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            QueueStore.Save(paths,
            [
                new Job
                {
                    Name = "move-job",
                    SourcePath = @"D:\photos",
                    DestinationPath = @"E:\",
                    Options = new JobOptions { Move = true }
                }
            ]);

            var loaded = QueueStore.Load(paths);
            Assert.True(Assert.Single(loaded).Options.Move);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void ShouldDeleteSourceWaitsForEveryDestination()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-move-gate-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(root, "src");
        var dest1 = Path.Combine(root, "dest1");
        var dest2 = Path.Combine(root, "dest2");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest1);
        Directory.CreateDirectory(dest2);
        var sourceFile = Path.Combine(src, "a.txt");
        File.WriteAllText(sourceFile, "alpha");
        var dest1File = Path.Combine(dest1, "a.txt");
        File.WriteAllText(dest1File, "alpha");
        try
        {
            var job = new Job { SourcePath = src };
            JobSources.Set(job, [src]);
            JobDestinations.Set(job, [dest1, dest2]);
            var dest1Row = new FileRecord
            {
                RelativePath = @"dest1\a.txt",
                SourcePath = sourceFile,
                DestPath = dest1File,
                Status = FileCopyStatus.Copied
            };
            Assert.False(SourceMover.ShouldDeleteSource(
                job, sourceFile, [dest1Row], destCount: 2, issuePaths: new HashSet<string>()));

            var dest2File = Path.Combine(dest2, "a.txt");
            File.WriteAllText(dest2File, "alpha");
            var dest2Row = new FileRecord
            {
                RelativePath = @"dest2\a.txt",
                SourcePath = sourceFile,
                DestPath = dest2File,
                Status = FileCopyStatus.Copied
            };
            Assert.True(SourceMover.ShouldDeleteSource(
                job, sourceFile, [dest1Row, dest2Row], destCount: 2, issuePaths: new HashSet<string>()));
            Assert.False(SourceMover.ShouldDeleteSource(
                job, sourceFile, [dest1Row, dest2Row], destCount: 2,
                issuePaths: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { dest2Row.RelativePath }));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task MoveDeletesSourceAfterVerify()
    {
        var (src, dest, paths) = CreateTrees();
        try
        {
            var a = Path.Combine(src, "a.txt");
            var b = Path.Combine(src, "b.txt");
            File.WriteAllText(a, "alpha");
            File.WriteAllText(b, "bravo");

            var job = NewJob(src, dest, move: true);
            await RunAsync(job, paths);

            Assert.True(job.Status == JobStatus.Completed, $"status={job.Status} msg={job.ResultMessage}");
            Assert.Contains("Moved complete", job.ResultMessage, StringComparison.Ordinal);
            var destFolder = Path.Combine(dest, Path.GetFileName(src));
            Assert.True(File.Exists(Path.Combine(destFolder, "a.txt")));
            Assert.True(File.Exists(Path.Combine(destFolder, "b.txt")));
            Assert.Equal("alpha", File.ReadAllText(Path.Combine(destFolder, "a.txt")));
            Assert.False(File.Exists(a));
            Assert.False(File.Exists(b));
            Assert.True(Directory.Exists(src));
        }
        finally
        {
            TryDelete(Directory.GetParent(src)!.FullName);
        }
    }

    [Fact]
    public async Task FailedVerifyKeepsSource()
    {
        var (src, dest, paths) = CreateTrees();
        try
        {
            var sourceFile = Path.Combine(src, "keep.txt");
            File.WriteAllText(sourceFile, "payload");
            var copy = NewJob(src, dest, move: false);
            await RunAsync(copy, paths);
            Assert.Equal(JobStatus.Completed, copy.Status);

            var destFile = Path.Combine(dest, Path.GetFileName(src), "keep.txt");
            File.WriteAllText(destFile, "changed-payload");

            var move = NewJob(src, dest, move: true);
            move.Options.Overwrite = OverwritePolicy.NeverIfExists;
            await RunAsync(move, paths);

            Assert.Equal(JobStatus.Incomplete, move.Status);
            Assert.True(File.Exists(sourceFile));
            Assert.True(move.IssueCount > 0);
            Assert.DoesNotContain("Moved complete", move.ResultMessage, StringComparison.Ordinal);
            Assert.DoesNotContain("Verified complete", move.ResultMessage, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(Directory.GetParent(src)!.FullName);
        }
    }

    [Fact]
    public async Task DryRunMoveDoesNotDeleteSource()
    {
        var (src, dest, paths) = CreateTrees();
        try
        {
            var sourceFile = Path.Combine(src, "keep.txt");
            File.WriteAllText(sourceFile, "payload");
            var job = NewJob(src, dest, move: true);
            job.Options.DryRun = true;
            await RunAsync(job, paths);

            Assert.Equal(JobStatus.Completed, job.Status);
            Assert.True(File.Exists(sourceFile));
            Assert.False(File.Exists(Path.Combine(dest, Path.GetFileName(src), "keep.txt")));
        }
        finally
        {
            TryDelete(Directory.GetParent(src)!.FullName);
        }
    }

    [Fact]
    public async Task MultiDestMoveDeletesSourceOnlyWhenBothDestsSucceed()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-move-2d-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(root, "src");
        var dest1 = Path.Combine(root, "dest1");
        var dest2 = Path.Combine(root, "dest2");
        var data = Path.Combine(root, "app");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest1);
        Directory.CreateDirectory(dest2);
        Directory.CreateDirectory(data);
        var sourceFile = Path.Combine(src, "a.txt");
        File.WriteAllText(sourceFile, "alpha");
        try
        {
            var job = NewMulti(src, [dest1, dest2], move: true);
            await RunAsync(job, new AppPaths(data));
            Assert.True(job.Status == JobStatus.Completed, $"status={job.Status} msg={job.ResultMessage}");
            Assert.Contains("Moved complete", job.ResultMessage, StringComparison.Ordinal);
            Assert.False(File.Exists(sourceFile));
            Assert.True(File.Exists(Path.Combine(dest1, Path.GetFileName(src), "a.txt")));
            Assert.True(File.Exists(Path.Combine(dest2, Path.GetFileName(src), "a.txt")));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task MultiDestMoveKeepsSourceWhenOneDestFails()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-move-1fail-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(root, "src");
        var dest1 = Path.Combine(root, "dest1");
        var dest2 = Path.Combine(root, "dest2");
        var data = Path.Combine(root, "app");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest1);
        Directory.CreateDirectory(data);
        var sourceFile = Path.Combine(src, "keep.txt");
        File.WriteAllText(sourceFile, "payload");
        File.WriteAllText(dest2, "not-a-folder");
        try
        {
            var job = NewMulti(src, [dest1, dest2], move: true);
            await RunAsync(job, new AppPaths(data));
            Assert.Equal(JobStatus.Incomplete, job.Status);
            Assert.True(File.Exists(sourceFile));
            Assert.True(File.Exists(Path.Combine(dest1, Path.GetFileName(src), "keep.txt")));
            Assert.DoesNotContain("Moved complete", job.ResultMessage, StringComparison.Ordinal);
            Assert.DoesNotContain("Verified complete", job.ResultMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task PackAsZipMoveDeletesSourceAfterUnpackVerify()
    {
        var (src, dest, paths) = CreateTrees();
        try
        {
            var a = Path.Combine(src, "a.txt");
            File.WriteAllText(a, "alpha");
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            var b = Path.Combine(src, "sub", "b.bin");
            File.WriteAllText(b, new string('x', 200));

            var job = NewJob(src, dest, move: true);
            job.Options.PackAsZip = true;
            await RunAsync(job, paths);

            Assert.True(job.Status == JobStatus.Completed, $"status={job.Status} msg={job.ResultMessage}");
            Assert.Contains("Moved complete", job.ResultMessage, StringComparison.Ordinal);
            var destFolder = Path.Combine(dest, Path.GetFileName(src));
            Assert.True(File.Exists(Path.Combine(destFolder, "a.txt")));
            Assert.True(File.Exists(Path.Combine(destFolder, "sub", "b.bin")));
            Assert.False(File.Exists(a));
            Assert.False(File.Exists(b));
            Assert.True(Directory.Exists(src));
            Assert.True(Directory.Exists(Path.Combine(src, "sub")));
        }
        finally
        {
            TryDelete(Directory.GetParent(src)!.FullName);
        }
    }

    private static (string src, string dest, AppPaths paths) CreateTrees()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-move-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(root, "src");
        var dest = Path.Combine(root, "dest");
        var data = Path.Combine(root, "app");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);
        Directory.CreateDirectory(data);
        return (src, dest, new AppPaths(data));
    }

    private static Job NewJob(string src, string dest, bool move) =>
        new()
        {
            Name = "move-test",
            SourcePath = src,
            DestinationPath = dest,
            SourceKind = SourceKind.Folder,
            Options = new JobOptions
            {
                RetryCount = 1,
                RetryWaitSeconds = 1,
                AdaptiveCopy = false,
                Move = move
            }
        };

    private static Job NewMulti(string src, IReadOnlyList<string> dests, bool move)
    {
        var job = new Job
        {
            Name = "move-multi",
            SourcePath = src,
            SourceKind = SourceKind.Folder,
            Options = new JobOptions
            {
                RetryCount = 1,
                RetryWaitSeconds = 1,
                AdaptiveCopy = false,
                Move = move
            }
        };
        JobSources.Set(job, [src]);
        JobDestinations.Set(job, dests);
        return job;
    }

    private static async Task RunAsync(Job job, AppPaths paths)
    {
        var scheduler = new JobScheduler(paths);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await scheduler.StartAsync(job, resumeJournal: false, cts.Token);
        var stored = scheduler.TryLoadLastJob();
        if (stored is not null)
        {
            job.Status = stored.Status;
            job.IssueCount = stored.IssueCount;
            job.ResultMessage = stored.ResultMessage;
        }
        else if (string.IsNullOrEmpty(job.ResultMessage))
        {
            job.ResultMessage = "journal missing after run";
        }

        scheduler.Dispose();
    }

    private static void TryDelete(string root)
    {
        try
        {
            if (Directory.Exists(root))
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
