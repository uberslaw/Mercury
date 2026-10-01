namespace Mercury.Tests;

public class JobDestinationsTests
{
    [Fact]
    public void RootsFallBackToDestinationPath()
    {
        var job = new Job { DestinationPath = @"Z:\backup" };
        Assert.Equal(@"Z:\backup", Assert.Single(JobDestinations.Roots(job)));
    }

    [Fact]
    public void SetDedupesAndKeepsDestinationPathFirst()
    {
        var job = new Job();
        JobDestinations.Set(job, [@"C:\a", @"C:\a\", @"C:\b"]);
        Assert.Equal(2, job.DestinationPaths.Count);
        Assert.Equal(job.DestinationPaths[0], job.DestinationPath, ignoreCase: true);
        Assert.Contains(" + 1 more", JobDestinations.Display(job), StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyListClearsDestinationPath()
    {
        var job = new Job { DestinationPath = @"Z:\backup" };
        JobDestinations.Set(job, []);
        Assert.Empty(JobDestinations.Roots(job));
        Assert.Equal("", job.DestinationPath);
        Assert.Empty(job.DestinationPaths);
    }

    [Fact]
    public void SameIgnoresOrder()
    {
        var job = new Job();
        JobDestinations.Set(job, [@"D:\one", @"D:\two"]);
        Assert.True(JobDestinations.Same(job, [@"D:\two", @"D:\one"]));
        Assert.False(JobDestinations.Same(job, [@"D:\one"]));
    }
}

public class CopyShapeMultiDestTests
{
    [Fact]
    public void OneDestKeepsSingleSourceJournalKeys()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-1dest-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(root, "Photos");
        var dest = Path.Combine(root, "backup");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);
        try
        {
            var mappings = CopyShape.ResolveFanOut([src], [dest], includeSourceFolderName: true);
            var mapping = Assert.Single(mappings);
            Assert.Equal("", mapping.UniqueRelativePrefix);
            Assert.Equal(Path.Combine(dest, "Photos"), mapping.DestRoot, ignoreCase: true);
            Assert.Equal(dest, mapping.UserDestPath, ignoreCase: true);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* leftover */ }
        }
    }

    [Fact]
    public void TwoDestsPrefixRelativePathsAndKeepSeparateLandings()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-2dest-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(root, "Photos");
        var dest1 = Path.Combine(root, "usb");
        var dest2 = Path.Combine(root, "nas");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest1);
        Directory.CreateDirectory(dest2);
        try
        {
            var mappings = CopyShape.ResolveFanOut([src], [dest1, dest2], includeSourceFolderName: true);
            Assert.Equal(2, mappings.Count);
            Assert.Equal(Path.Combine(dest1, "Photos"), mappings[0].DestRoot, ignoreCase: true);
            Assert.Equal(Path.Combine(dest2, "Photos"), mappings[1].DestRoot, ignoreCase: true);
            Assert.False(string.Equals(mappings[0].UniqueRelativePrefix, mappings[1].UniqueRelativePrefix, StringComparison.OrdinalIgnoreCase));
            Assert.False(string.IsNullOrEmpty(mappings[0].UniqueRelativePrefix));
            var srcFile = Path.Combine(src, "a.txt");
            File.WriteAllText(srcFile, "x");
            var dest1File = new FileRecord
            {
                RelativePath = Path.Combine(mappings[0].UniqueRelativePrefix, "a.txt"),
                SourcePath = srcFile,
                DestPath = Path.Combine(mappings[0].DestRoot, "a.txt")
            };
            var dest2File = new FileRecord
            {
                RelativePath = Path.Combine(mappings[1].UniqueRelativePrefix, "a.txt"),
                SourcePath = srcFile,
                DestPath = Path.Combine(mappings[1].DestRoot, "a.txt")
            };
            Assert.Equal(mappings[0].DestRoot, CopyShape.FindMapping(dest1File, mappings).DestRoot, ignoreCase: true);
            Assert.Equal(mappings[1].DestRoot, CopyShape.FindMapping(dest2File, mappings).DestRoot, ignoreCase: true);
            Assert.Contains("usb", CopyShape.PreviewLandingSummary([src], [dest1, dest2]));
            Assert.Contains("nas", CopyShape.PreviewLandingSummary([src], [dest1, dest2]));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* leftover */ }
        }
    }

    [Fact]
    public void ResolveFanOutRejectsEmptyDestList()
    {
        var ex = Assert.Throws<ArgumentException>(() => CopyShape.ResolveFanOut([@"D:\src"], []));
        Assert.Contains("destination", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}

public class MultiDestPersistenceTests
{
    [Fact]
    public void QueueStoreRoundtripsDestinationPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-qdst-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            var job = new Job { SourcePath = @"D:\photos" };
            JobDestinations.Set(job, [@"Z:\backup", @"E:\usb"]);
            QueueStore.Save(paths, [job]);
            var loaded = QueueStore.Load(paths);
            Assert.Equal(2, loaded[0].DestinationPaths.Count);
            Assert.Contains(@"E:\usb", loaded[0].DestinationPaths);
            Assert.Equal(loaded[0].DestinationPaths[0], loaded[0].DestinationPath, ignoreCase: true);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* leftover */ }
        }
    }

    [Fact]
    public void JobJournalRoundtripsDestinationPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-jdst-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var job = new Job { Id = "job-dst", SourcePath = @"D:\photos" };
            JobDestinations.Set(job, [@"Z:\backup", @"E:\usb"]);
            using (var journal = JobJournal.Create(root, job))
            {
                journal.SaveJob(job);
            }

            using var opened = JobJournal.Open(root);
            var loaded = opened.LoadJob();
            Assert.Equal(2, loaded.DestinationPaths.Count);
            Assert.Contains(@"E:\usb", loaded.DestinationPaths);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* leftover */ }
        }
    }

    [Fact]
    public void LibraryStoreRoundtripsSavedJobDestinationPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-saveddst-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            LibraryStore.SaveSavedJobs(paths,
            [
                new SavedJob
                {
                    Name = "two dests",
                    SourcePath = @"D:\photos",
                    SourcePaths = [@"D:\photos"],
                    DestinationPath = @"Z:\backup",
                    DestinationPaths = [@"Z:\backup", @"E:\usb"]
                }
            ]);
            var loaded = LibraryStore.LoadSavedJobs(paths);
            Assert.Equal(2, loaded[0].DestinationPaths.Count);
            Assert.Contains(@"E:\usb", loaded[0].DestinationPaths);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* leftover */ }
        }
    }
}

public class MultiDestCopyTests
{
    [Fact]
    public async Task CopyWritesTheSameSourceToTwoDestinations()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-copy2d-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(root, "src");
        var dest1 = Path.Combine(root, "dest1");
        var dest2 = Path.Combine(root, "dest2");
        var data = Path.Combine(root, "app");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest1);
        Directory.CreateDirectory(dest2);
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(src, "a.txt"), "alpha");
        try
        {
            var job = NewJob(src, [dest1, dest2]);
            await RunAsync(job, new AppPaths(data));
            Assert.True(job.Status == JobStatus.Completed, $"status={job.Status} msg={job.ResultMessage}");
            Assert.True(File.Exists(Path.Combine(dest1, Path.GetFileName(src), "a.txt")));
            Assert.True(File.Exists(Path.Combine(dest2, Path.GetFileName(src), "a.txt")));
            Assert.Equal("alpha", File.ReadAllText(Path.Combine(dest1, Path.GetFileName(src), "a.txt")));
            Assert.Equal("alpha", File.ReadAllText(Path.Combine(dest2, Path.GetFileName(src), "a.txt")));
            Assert.Contains("2 destinations", job.ResultMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task CopyContinuesWhenOneDestinationFails()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-copy1fail-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(root, "src");
        var dest1 = Path.Combine(root, "dest1");
        var dest2 = Path.Combine(root, "dest2");
        var data = Path.Combine(root, "app");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest1);
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(src, "keep.txt"), "payload");
        File.WriteAllText(dest2, "not-a-folder");
        try
        {
            var job = NewJob(src, [dest1, dest2]);
            await RunAsync(job, new AppPaths(data));
            Assert.Equal(JobStatus.Incomplete, job.Status);
            Assert.True(File.Exists(Path.Combine(dest1, Path.GetFileName(src), "keep.txt")));
            Assert.True(File.Exists(dest2) && !Directory.Exists(dest2));
            Assert.True(job.IssueCount > 0);
            Assert.Contains("Incomplete", job.ResultMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Verified", job.ResultMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static Job NewJob(string src, IReadOnlyList<string> dests)
    {
        var job = new Job
        {
            Name = "multi-dest",
            SourcePath = src,
            SourceKind = SourceKind.Folder,
            Options = new JobOptions
            {
                RetryCount = 1,
                RetryWaitSeconds = 1,
                AdaptiveCopy = false
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
