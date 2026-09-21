namespace Mercury.Tests;

public class JobDueTests
{
    [Fact]
    public void NotDueUntilScheduledStart()
    {
        var now = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.FromHours(10));
        var job = new Job
        {
            Status = JobStatus.Pending,
            ScheduledStart = now.AddHours(1)
        };

        Assert.False(JobDue.IsDue(job, now));
        Assert.True(JobDue.IsDue(job, job.ScheduledStart.Value));
        Assert.True(JobDue.IsDue(job, job.ScheduledStart.Value.AddMinutes(1)));
    }

    [Fact]
    public void HoursWindowAndCalendarStartBothApply()
    {
        var saturday7 = new DateTimeOffset(new DateTime(2026, 9, 19, 7, 0, 0, DateTimeKind.Local));
        var saturday9 = new DateTimeOffset(new DateTime(2026, 9, 19, 9, 0, 0, DateTimeKind.Local));
        var job = new Job
        {
            Status = JobStatus.Pending,
            ScheduledStart = new DateTimeOffset(new DateTime(2026, 9, 19, 0, 0, 0, DateTimeKind.Local)),
            Options = new JobOptions
            {
                HoursEnabled = true,
                HoursStart = new TimeOnly(8, 0),
                HoursEnd = new TimeOnly(18, 0)
            }
        };

        Assert.False(JobDue.IsDue(job, saturday7));
        Assert.True(JobDue.IsDue(job, saturday9));
    }

    [Fact]
    public void FindNextDoesNotJumpANotDueHead()
    {
        var now = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.FromHours(10));
        var waiting = new Job
        {
            Name = "later",
            Status = JobStatus.Pending,
            ScheduledStart = now.AddHours(5)
        };
        var due = new Job { Name = "now", Status = JobStatus.Pending };
        var done = new Job { Name = "done", Status = JobStatus.Completed };

        Assert.Null(JobDue.FindNext([waiting, done, due], now));
    }

    [Fact]
    public void FindNextSkipsOnHoldAndFinishedThenStartsNextDue()
    {
        var now = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.FromHours(10));
        var done = new Job { Name = "done", Status = JobStatus.Completed };
        var held = new Job { Name = "held", Status = JobStatus.Pending, OnHold = true };
        var due = new Job { Name = "now", Status = JobStatus.Pending };

        var next = JobDue.FindNext([done, held, due], now);
        Assert.Same(due, next);
    }

    [Fact]
    public void FindNextForceStartIgnoresOrderAndDue()
    {
        var now = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.FromHours(10));
        var first = new Job { Name = "first", Status = JobStatus.Pending, ScheduledStart = now.AddHours(2) };
        var held = new Job { Id = "force-me", Name = "held", Status = JobStatus.Pending, OnHold = true };
        var later = new Job { Id = "force-me", Name = "later", Status = JobStatus.Pending, ScheduledStart = now.AddHours(5) };

        Assert.Null(JobDue.FindNext([first, later], now));
        var forced = JobDue.FindNext([first, later], now, forceJobId: "force-me");
        Assert.Same(later, forced);
        Assert.Null(JobDue.FindNext([first, held], now, forceJobId: "force-me"));
    }

    [Fact]
    public void FindNextTimerDoesNotStartUnscheduledPending()
    {
        var now = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.FromHours(10));
        var waiting = new Job { Name = "queued", Status = JobStatus.Pending };
        Assert.Same(waiting, JobDue.FindNext([waiting], now, includeUnscheduled: true));
        Assert.Null(JobDue.FindNext([waiting], now, includeUnscheduled: false));
        var scheduled = new Job
        {
            Name = "timed",
            Status = JobStatus.Pending,
            ScheduledStart = now.AddMinutes(-1)
        };
        Assert.Same(scheduled, JobDue.FindNext([scheduled], now, includeUnscheduled: false));
    }

    [Fact]
    public void FindNextForceStartRunsTheClickedJob()
    {
        var now = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.FromHours(10));
        var first = new Job { Name = "first", Status = JobStatus.Pending, ScheduledStart = now.AddHours(2) };
        var started = new Job { Id = "start-now", Name = "started", Status = JobStatus.Pending };
        Assert.Same(started, JobDue.FindNext([first, started], now, forceJobId: "start-now"));
    }

    [Fact]
    public void QueueStoreRoundtripsScheduledStart()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-queue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            var start = new DateTimeOffset(2026, 9, 19, 9, 0, 0, TimeSpan.FromHours(10));
            QueueStore.Save(paths,
            [
                new Job
                {
                    Name = "weekend",
                    SourcePath = @"D:\src",
                    DestinationPath = @"E:\dst",
                    ScheduledStart = start,
                    Options = new JobOptions { HoursEnabled = true }
                }
            ]);

            var loaded = QueueStore.Load(paths);
            Assert.Single(loaded);
            Assert.Equal("weekend", loaded[0].Name);
            Assert.Equal(start, loaded[0].ScheduledStart);
            Assert.True(loaded[0].Options.HoursEnabled);
            Assert.False(loaded[0].Options.IgnoreFreeSpaceCheck);
            Assert.False(loaded[0].Options.PackAsZip);
            Assert.False(loaded[0].OnHold);
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
    public void QueueStoreRoundtripsIgnoreFreeSpaceCheck()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-queue-expand-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            QueueStore.Save(paths,
            [
                new Job
                {
                    Name = "thin",
                    SourcePath = @"D:\",
                    DestinationPath = @"J:\incoming",
                    Options = new JobOptions { IgnoreFreeSpaceCheck = true, DryRun = false }
                }
            ]);

            var loaded = QueueStore.Load(paths);
            Assert.Single(loaded);
            Assert.True(loaded[0].Options.IgnoreFreeSpaceCheck);
            Assert.False(loaded[0].Options.DryRun);
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
    public void QueueStoreRoundtripsOnHold()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-queue-hold-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            QueueStore.Save(paths,
            [
                new Job
                {
                    Name = "parked",
                    SourcePath = @"D:\src",
                    DestinationPath = @"E:\dst",
                    Status = JobStatus.Pending,
                    OnHold = true
                }
            ]);

            var loaded = QueueStore.Load(paths);
            Assert.Single(loaded);
            Assert.True(loaded[0].OnHold);
            Assert.Equal("On hold", JobDue.StatusLabel(loaded[0], DateTimeOffset.Now));
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
    public void QueueStoreRoundtripsPackAsZip()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-queue-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            QueueStore.Save(paths,
            [
                new Job
                {
                    Name = "usb",
                    SourcePath = @"D:\photos",
                    DestinationPath = @"E:\",
                    Options = new JobOptions { PackAsZip = true }
                }
            ]);

            var loaded = QueueStore.Load(paths);
            Assert.Single(loaded);
            Assert.True(loaded[0].Options.PackAsZip);
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
}

public class LiveSpeedTests
{
    [Fact]
    public void MaxBytesPerSecondTracksLiveOptions()
    {
        var options = new JobOptions { MaxMegabytesPerSecond = 10 };
        var job = new Job { Options = options };
        Assert.Equal(10 * 1024 * 1024, job.Options.MaxBytesPerSecond);

        job.Options.MaxMegabytesPerSecond = 2;
        Assert.Equal(2 * 1024 * 1024, job.Options.MaxBytesPerSecond);

        job.Options.MaxMegabytesPerSecond = null;
        Assert.Null(job.Options.MaxBytesPerSecond);
    }
}

public class JobOptionBadgeTests
{
    [Fact]
    public void DefaultJobHasNoContentsOnlyBadge()
    {
        var job = new Job { SourcePath = @"D:\Anchor Span", DestinationPath = @"Z:\EngA" };
        Assert.DoesNotContain("Contents only", JobOptionBadges.For(job));
        Assert.True(job.Options.IncludeSourceFolderName);
    }

    [Fact]
    public void WrapOffShowsContentsOnlyBadge()
    {
        var job = new Job
        {
            Options = new JobOptions
            {
                IncludeSourceFolderName = false,
                DryRun = true,
                IgnoreFreeSpaceCheck = true
            },
            OnHold = true
        };
        var badges = JobOptionBadges.For(job);
        Assert.Contains("Contents only", badges);
        Assert.Contains("Dry Run", badges);
        Assert.Contains("Ignore Storage Limit", badges);
        Assert.Contains("Hold", badges);
        Assert.DoesNotContain("Verify Quick", badges);
        Assert.DoesNotContain("Unlimited speed", badges, StringComparer.OrdinalIgnoreCase);
    }
}

public class BrowseMemoryTests
{
    [Fact]
    public void SourceBrowseDoesNotUseLastDest()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-browse-sep-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(root, "src");
        var dest = Path.Combine(root, "dest");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);
        try
        {
            var recents = new RecentLocations
            {
                LastDestDir = dest,
                LastSourceDir = src
            };

            var start = LibraryStore.BrowseStartDir(isSource: true, recents, currentPath: dest);
            Assert.Equal(src, start);
            Assert.Equal(dest, LibraryStore.BrowseStartDir(isSource: false, recents, currentPath: src));
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
    public void RecentsRoundtripLastSourceAndDestDirs()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-browse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var src = Path.Combine(root, "src");
        var dest = Path.Combine(root, "dest");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);
        try
        {
            var paths = new AppPaths(root);
            var recents = new RecentLocations();
            LibraryStore.Remember(recents.Sources, Path.Combine(src, "a.txt"));
            LibraryStore.RememberBrowseDir(recents, isSource: true, src);
            LibraryStore.Remember(recents.Destinations, dest);
            LibraryStore.RememberBrowseDir(recents, isSource: false, dest);
            LibraryStore.SaveRecents(paths, recents);

            var loaded = LibraryStore.LoadRecents(paths);
            Assert.Equal(src, loaded.LastSourceDir);
            Assert.Equal(dest, loaded.LastDestDir);
            Assert.Contains(src, loaded.Sources[0], StringComparison.OrdinalIgnoreCase);
            Assert.Equal(dest, loaded.Destinations[0]);
            Assert.Equal(src, LibraryStore.BrowseStartDir(true, loaded, currentPath: dest));
            Assert.Equal(dest, LibraryStore.BrowseStartDir(false, loaded, currentPath: src));
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
}

public class IdleThrottleSettingsTests
{
    [Fact]
    public void SettingsRoundtripIdleThrottle()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-idle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            AppSettingsStore.Save(paths, new BandwidthSettings
            {
                GlobalMaxMegabytesPerSecond = 80,
                IdleThrottleMegabytesPerSecond = 12,
                IdleCpuPercentThreshold = 18,
                ShowSpeedInMegabits = true
            });

            var loaded = AppSettingsStore.Load(paths);
            Assert.Equal(80, loaded.GlobalMaxMegabytesPerSecond);
            Assert.Equal(12, loaded.IdleThrottleMegabytesPerSecond);
            Assert.Equal(18, loaded.IdleCpuPercentThreshold);
            Assert.True(loaded.ShowSpeedInMegabits);
            Assert.Equal(12 * 1024 * 1024, loaded.IdleThrottleBytesPerSecond);
            Assert.Contains("showSpeedInMegabits", File.ReadAllText(paths.SettingsFile), StringComparison.Ordinal);
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
}
