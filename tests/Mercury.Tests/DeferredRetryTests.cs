namespace Mercury.Tests;

public class DeferredRetryTests
{
    [Fact]
    public void QuarterHitsAt25And100()
    {
        Assert.Equal(0, DeferredRetry.Quarter(0, 16_000));
        Assert.Equal(1, DeferredRetry.Quarter(4_000, 16_000));
        Assert.Equal(2, DeferredRetry.Quarter(8_000, 16_000));
        Assert.Equal(3, DeferredRetry.Quarter(12_000, 16_000));
        Assert.Equal(4, DeferredRetry.Quarter(16_000, 16_000));
        Assert.Equal(0, DeferredRetry.Quarter(100, 0));
    }

    [Fact]
    public void FewFilesPreferEndOfPass()
    {
        Assert.True(DeferredRetry.PreferFileBoundary(3, 3000, 1000));
        Assert.True(DeferredRetry.PreferFileBoundary(8, 8000, 100));
        Assert.False(DeferredRetry.PreferFileBoundary(16, 16_000, 100));
        Assert.True(DeferredRetry.PreferFileBoundary(16, 16_000, 4000));
    }

    [Fact]
    public void SharingViolationIsTransient()
    {
        var ex = new IOException("The process cannot access the file because it is being used by another process.");
        Assert.True(DeferredRetry.IsTransient(ex));
        Assert.False(DeferredRetry.IsTransient(new IOException("There is not enough space on the disk.")));
    }

    [Fact]
    public void ProgressHeaderHidesOverallForOneJob()
    {
        Assert.False(ProgressHeader.ShowOverall(1));
        Assert.True(ProgressHeader.ShowOverall(2));
    }

    [Fact]
    public async Task DeferredFileSucceedsAt25Percent()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-def25-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(root, "src");
        var dest = Path.Combine(root, "dest");
        var data = Path.Combine(root, "app");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);
        Directory.CreateDirectory(data);
        FileStream? lockStream = null;
        try
        {
            for (var i = 0; i < 16; i++)
            {
                File.WriteAllBytes(Path.Combine(src, $"{i:00}.bin"), new byte[1000]);
            }

            var locked = Path.Combine(src, "00.bin");
            lockStream = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            var job = new Job
            {
                Name = "deferred-25",
                SourcePath = src,
                DestinationPath = dest,
                Options = new JobOptions { RetryCount = 0, RetryWaitSeconds = 1 }
            };
            var paths = new AppPaths(data);
            var scheduler = new JobScheduler(paths);
            var lines = new List<string>();
            var unlocked = false;
            scheduler.Log.LineWritten += ev =>
            {
                lines.Add(ev.Message);
                if (!unlocked && ev.Message.StartsWith("Deferred ", StringComparison.Ordinal))
                {
                    lockStream?.Dispose();
                    lockStream = null;
                    unlocked = true;
                }
            };
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await scheduler.StartAsync(job, resumeJournal: false, cts.Token);
            scheduler.Dispose();

            var stored = new JobScheduler(paths).TryLoadLastJob();
            Assert.True(unlocked);
            Assert.Contains(lines, l => l.Contains("Retrying deferred files", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.Contains("25% progress", StringComparison.Ordinal) || l.Contains("end of pass", StringComparison.Ordinal));
            Assert.Equal(JobStatus.Completed, stored?.Status ?? job.Status);
            Assert.True(File.Exists(Path.Combine(dest, Path.GetFileName(src), "00.bin")));
        }
        finally
        {
            lockStream?.Dispose();
            TryDelete(root);
        }
    }

    [Fact]
    public async Task FewFileJobRetriesAtEndAndCanSucceed()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-defend-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(root, "src");
        var dest = Path.Combine(root, "dest");
        var data = Path.Combine(root, "app");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);
        Directory.CreateDirectory(data);
        FileStream? lockStream = null;
        try
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "one");
            File.WriteAllText(Path.Combine(src, "b.txt"), "two");
            File.WriteAllText(Path.Combine(src, "c.txt"), "three");
            lockStream = new FileStream(Path.Combine(src, "a.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            var job = new Job
            {
                Name = "deferred-end",
                SourcePath = src,
                DestinationPath = dest,
                Options = new JobOptions { RetryCount = 0, RetryWaitSeconds = 1 }
            };
            var paths = new AppPaths(data);
            var scheduler = new JobScheduler(paths);
            var lines = new List<string>();
            scheduler.Log.LineWritten += ev =>
            {
                lines.Add(ev.Message);
                if (ev.Message.StartsWith("Deferred ", StringComparison.Ordinal))
                {
                    lockStream?.Dispose();
                    lockStream = null;
                }
            };
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await scheduler.StartAsync(job, resumeJournal: false, cts.Token);
            scheduler.Dispose();
            Assert.Contains(lines, l => l.Contains("Retrying deferred files", StringComparison.Ordinal));
            var stored = new JobScheduler(paths).TryLoadLastJob();
            Assert.Equal(JobStatus.Completed, stored?.Status ?? job.Status);
        }
        finally
        {
            lockStream?.Dispose();
            TryDelete(root);
        }
    }

    [Fact]
    public async Task StillFailingDeferredFilesRemainInIssues()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-deffail-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(root, "src");
        var dest = Path.Combine(root, "dest");
        var data = Path.Combine(root, "app");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);
        Directory.CreateDirectory(data);
        using var lockStream = new FileStream(
            Path.Combine(src, "locked.txt"),
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.None);
        lockStream.WriteByte(1);
        lockStream.Flush();
        File.WriteAllText(Path.Combine(src, "ok.txt"), "ok");
        try
        {
            var job = new Job
            {
                Name = "deferred-fail",
                SourcePath = src,
                DestinationPath = dest,
                Options = new JobOptions { RetryCount = 0, RetryWaitSeconds = 1 }
            };
            var paths = new AppPaths(data);
            using var scheduler = new JobScheduler(paths);
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await scheduler.StartAsync(job, resumeJournal: false, cts.Token);
            var stored = scheduler.TryLoadLastJob();
            Assert.Equal(JobStatus.Incomplete, stored?.Status ?? job.Status);
            Assert.True((stored?.IssueCount ?? job.IssueCount) > 0);
            using var journal = JobJournal.Open(paths.JobDirectory(job.Id));
            Assert.Contains(journal.GetIssues(), i => i.RelativePath != null && i.RelativePath.Contains("locked", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDelete(root);
        }
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

public class AppPathsTests
{
    [Fact]
    public void NewMachineUsesRoamingAppData()
    {
        var exe = Path.Combine(Path.GetTempPath(), "mercury-exe-" + Guid.NewGuid().ToString("N"));
        var roaming = Path.Combine(Path.GetTempPath(), "mercury-roam-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(exe);
        try
        {
            var resolved = AppPaths.ResolveProduction(exe, roaming);
            Assert.Equal(roaming, resolved.Root);
            Assert.False(resolved.Portable);
            Assert.False(resolved.Legacy);
        }
        finally
        {
            TryDelete(exe);
            TryDelete(roaming);
        }
    }

    [Fact]
    public void KeepsBesideExeJournalsWhenAppDataEmpty()
    {
        var exe = Path.Combine(Path.GetTempPath(), "mercury-exe2-" + Guid.NewGuid().ToString("N"));
        var roaming = Path.Combine(Path.GetTempPath(), "mercury-roam2-" + Guid.NewGuid().ToString("N"));
        var portable = Path.Combine(exe, "data");
        var jobDir = Path.Combine(portable, "jobs", "abc");
        Directory.CreateDirectory(jobDir);
        File.WriteAllText(Path.Combine(jobDir, "job.db"), "x");
        try
        {
            var resolved = AppPaths.ResolveProduction(exe, roaming);
            Assert.Equal(portable, resolved.Root);
            Assert.True(resolved.Portable);
            Assert.True(resolved.Legacy);
        }
        finally
        {
            TryDelete(exe);
            TryDelete(roaming);
        }
    }

    [Fact]
    public void PortableFlagWins()
    {
        var exe = Path.Combine(Path.GetTempPath(), "mercury-exe3-" + Guid.NewGuid().ToString("N"));
        var roaming = Path.Combine(Path.GetTempPath(), "mercury-roam3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(exe);
        AppPaths.SetPortablePreferred(exe, true);
        try
        {
            var resolved = AppPaths.ResolveProduction(exe, roaming);
            Assert.Equal(Path.Combine(exe, "data"), resolved.Root);
            Assert.True(resolved.Portable);
            Assert.False(resolved.Legacy);
        }
        finally
        {
            TryDelete(exe);
            TryDelete(roaming);
        }
    }

    [Fact]
    public void PauseAfterFileWaitsUntilApplied()
    {
        var gate = new PauseGate();
        gate.BeginFile("big.bin", 10_000);
        gate.AddFileBytes(1000);
        Assert.Null(gate.RemainingFileEta(0));
        var eta = gate.RemainingFileEta(1000);
        Assert.NotNull(eta);
        Assert.True(eta.Value.TotalSeconds >= 8);
        gate.RequestPauseAfterFile();
        Assert.True(gate.PauseAfterFileRequested);
        gate.CancelPauseAfterFile();
        Assert.False(gate.PauseAfterFileRequested);
        Assert.False(gate.TryApplyPauseAfterFile());
        Assert.False(gate.IsPaused);
        gate.RequestPauseAfterFile();
        Assert.True(gate.TryApplyPauseAfterFile());
        Assert.True(gate.IsPaused);
        Assert.False(gate.PauseAfterFileRequested);
    }

    [Fact]
    public void EffectiveRateUsesBytesOverElapsedAfterWarmup()
    {
        Assert.Equal(0, ByteFormatter.EffectiveRate(0, 10_000, TimeSpan.FromSeconds(2)));
        Assert.Equal(10_000d / 3, ByteFormatter.EffectiveRate(0, 10_000, TimeSpan.FromSeconds(3)));
        Assert.Equal(100, ByteFormatter.EffectiveRate(100, 10_000, TimeSpan.FromHours(1)));
        var packed = ByteFormatter.EffectiveRate(0, 10L * 1024 * 1024 * 1024, TimeSpan.FromMinutes(31));
        Assert.True(packed > 1_000_000, $"rate={packed}");
    }

    [Fact]
    public void HelpListsCatcherAndSettings()
    {
        Assert.Contains(HelpDocument.Sections, s => s.Id == "catcher");
        Assert.Contains(HelpDocument.Sections, s => s.Id == "settings");
        Assert.Contains(HelpDocument.Sections, s => s.Id == "roboflags");
        Assert.Contains(HelpDocument.Search("passphrase"), s => s.Id == "catcher");
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
