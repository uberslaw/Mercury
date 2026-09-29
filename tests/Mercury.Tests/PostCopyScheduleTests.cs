namespace Mercury.Tests;

public class PostCopyScheduleTests
{
    [Fact]
    public void LongNameIsTruncatedThenSuffixed()
    {
        var stem = new string('n', 252);
        var original = stem + ".txt";
        Assert.True(original.Length > DestNameAdjuster.MaxSegmentLength);
        var files = new List<FileRecord> { FileAt(@"C:\in", original) };

        var notes = DestNameAdjuster.Resolve(files, fix: true);

        var name = NameOf(files[0].DestPath);
        Assert.True(name.Length <= DestNameAdjuster.MaxSegmentLength);
        Assert.EndsWith(" - file-transfer.txt", name);
        Assert.StartsWith(new string('n', 200), name);
        Assert.Single(notes);
        Assert.Contains("length", notes[0].Reason);
        Assert.False(notes[0].Skipped);
        Assert.Equal(name, NameOf(files[0].RelativePath));
        Assert.True(files[0].DestPath.Length <= DestNameAdjuster.MaxFullPathLength);
    }

    [Fact]
    public void FullDestPathOver259IsShortened()
    {
        var fileName = new string('b', 20) + ".txt";
        var folder = new string('d', 232);
        var dest = @"C:\" + folder + @"\" + fileName;
        Assert.True(dest.Length > DestNameAdjuster.MaxFullPathLength);
        var files = new List<FileRecord>
        {
            new()
            {
                RelativePath = fileName,
                DestPath = dest,
                SourcePath = @"D:\src\" + fileName,
                Status = FileCopyStatus.Pending
            }
        };

        var notes = DestNameAdjuster.Resolve(files, fix: true);

        Assert.False(notes[0].Skipped);
        Assert.Contains("length", notes[0].Reason);
        Assert.True(files[0].DestPath.Length <= DestNameAdjuster.MaxFullPathLength);
        Assert.True(NameOf(files[0].DestPath).Length <= DestNameAdjuster.MaxSegmentLength);
        Assert.Contains(" - file-transfer", NameOf(files[0].DestPath));
    }

    [Fact]
    public void LongPathPrefixSkipsTheFullPathLimitButNotTheSegmentLimit()
    {
        var shortName = "ok.txt";
        var prefixed = @"\\?\C:\folder\" + shortName;
        Assert.True(prefixed.Length < 100);
        var fine = new List<FileRecord> { new() { RelativePath = shortName, DestPath = prefixed, SourcePath = @"D:\s\" + shortName } };
        Assert.Empty(DestNameAdjuster.Resolve(fine, fix: true));

        var longName = new string('q', 256) + ".txt";
        var longPrefixed = @"\\?\C:\folder\" + longName;
        var files = new List<FileRecord> { new() { RelativePath = longName, DestPath = longPrefixed, SourcePath = @"D:\s\" + longName } };
        var notes = DestNameAdjuster.Resolve(files, fix: true);
        Assert.Contains("length", notes[0].Reason);
        Assert.True(NameOf(files[0].DestPath).Length <= DestNameAdjuster.MaxSegmentLength);
        Assert.StartsWith(@"\\?\C:\folder\", files[0].DestPath);
    }

    [Fact]
    public void DuplicateDestNamesGetFileTransferSuffix()
    {
        var files = new List<FileRecord>
        {
            FileAt(@"E:\box", "report.pdf", @"D:\one\report.pdf"),
            FileAt(@"E:\box", "report.pdf", @"D:\two\report.pdf")
        };

        var notes = DestNameAdjuster.Resolve(files, fix: true);

        Assert.Equal("report.pdf", files[0].RelativePath);
        Assert.Equal("report - file-transfer.pdf", files[1].RelativePath);
        Assert.Equal(@"E:\box\report - file-transfer.pdf", files[1].DestPath);
        Assert.Equal(@"D:\two\report.pdf", files[1].SourcePath);
        Assert.Contains("duplicate", notes[0].Reason);
        Assert.False(notes[0].Skipped);
    }

    [Fact]
    public void SuffixCollisionUsesFileTransferTwo()
    {
        var files = new List<FileRecord>
        {
            FileAt(@"E:\box", "a.txt", @"D:\src\one.txt"),
            FileAt(@"E:\box", "a - file-transfer.txt", @"D:\src\two.txt"),
            FileAt(@"E:\box", "a.txt", @"D:\src\three.txt")
        };

        var notes = DestNameAdjuster.Resolve(files, fix: true);

        Assert.Equal("a.txt", files[0].RelativePath);
        Assert.Equal("a - file-transfer.txt", files[1].RelativePath);
        Assert.Equal("a - file-transfer (2).txt", files[2].RelativePath);
        Assert.Equal(@"E:\box\a - file-transfer (2).txt", files[2].DestPath);
        Assert.Contains(notes, n => n.AdjustedRelative.EndsWith("a - file-transfer (2).txt", StringComparison.Ordinal));
    }

    [Fact]
    public void FixOffSkipsDuplicatesInsteadOfOverwriting()
    {
        var files = new List<FileRecord>
        {
            FileAt(@"E:\box", "a.txt", @"D:\one\a.txt"),
            FileAt(@"E:\box", "A.TXT", @"D:\two\a.txt")
        };

        var notes = DestNameAdjuster.Resolve(files, fix: false);

        Assert.Equal(FileCopyStatus.Pending, files[0].Status);
        Assert.Equal("a.txt", files[0].RelativePath);
        Assert.Equal(FileCopyStatus.Skipped, files[1].Status);
        Assert.Equal("A.TXT", files[1].RelativePath);
        Assert.True(notes[0].Skipped);
        Assert.Contains("duplicate", notes[0].Reason);
    }

    [Fact]
    public void JournalRenameStaysInsideDestination()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-rename-" + Guid.NewGuid().ToString("N"));
        var destRoot = Path.Combine(root, "dest");
        var src = Path.Combine(root, "src");
        Directory.CreateDirectory(destRoot);
        Directory.CreateDirectory(src);
        var original = Path.Combine(destRoot, "a.txt");
        File.WriteAllText(original, "hello");
        var outside = Path.Combine(root, "nope.txt");
        try
        {
            var job = new Job
            {
                DestinationPath = destRoot,
                SourcePath = src,
                Status = JobStatus.Completed
            };
            using var journal = JobJournal.Create(Path.Combine(root, "job"), job);
            journal.UpsertFile(new FileRecord
            {
                RelativePath = "a.txt",
                SourcePath = Path.Combine(src, "a.txt"),
                DestPath = original,
                Size = 5,
                LastWriteUtc = DateTime.UtcNow,
                Status = FileCopyStatus.Copied
            });

            Assert.False(DestFileEdit.IsInsideDestination(destRoot, outside));
            var escaped = DestFileEdit.Apply(job, journal, "a.txt", ".." + Path.DirectorySeparatorChar + "nope.txt", DateTime.Now);
            Assert.False(escaped.Ok);
            Assert.True(File.Exists(original));
            Assert.False(File.Exists(outside));
            Assert.Equal("a.txt", journal.GetFiles().Single().RelativePath);

            var dotdot = DestFileEdit.Apply(job, journal, "a.txt", "..", DateTime.Now);
            Assert.False(dotdot.Ok);
            Assert.True(File.Exists(original));

            var when = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Local);
            var renamed = DestFileEdit.Apply(job, journal, "a.txt", "b.txt", when);
            Assert.True(renamed.Ok, renamed.Error);
            var moved = Path.Combine(destRoot, "b.txt");
            Assert.False(File.Exists(original));
            Assert.True(File.Exists(moved));
            Assert.True(DestFileEdit.IsInsideDestination(destRoot, moved));
            var saved = journal.GetFiles().Single();
            Assert.Equal("b.txt", saved.RelativePath);
            Assert.Equal(moved, saved.DestPath);
            var delta = File.GetLastWriteTimeUtc(moved) - saved.LastWriteUtc;
            Assert.True(delta.Duration() < TimeSpan.FromSeconds(2));
            var createdDelta = File.GetCreationTimeUtc(moved) - File.GetLastWriteTimeUtc(moved);
            Assert.True(createdDelta.Duration() < TimeSpan.FromSeconds(2));

            File.WriteAllText(Path.Combine(destRoot, "c.txt"), "x");
            journal.UpsertFile(new FileRecord
            {
                RelativePath = "c.txt",
                SourcePath = Path.Combine(src, "c.txt"),
                DestPath = Path.Combine(destRoot, "c.txt"),
                Size = 1,
                LastWriteUtc = DateTime.UtcNow,
                Status = FileCopyStatus.Copied
            });
            var collide = DestFileEdit.Apply(job, journal, "b.txt", "c.txt", when);
            Assert.False(collide.Ok);
            Assert.Contains("exists", collide.Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(moved));
            Assert.Equal("b.txt", journal.GetFiles().Single(f => f.DestPath == moved).RelativePath);
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
                // temp leftover
            }
        }
    }

    [Fact]
    public void SchedulerSkipsADisallowedWeekday()
    {
        var saturday = new DateTimeOffset(new DateTime(2026, 9, 19, 10, 0, 0, DateTimeKind.Local));
        Assert.Equal(DayOfWeek.Saturday, saturday.DayOfWeek);
        var weekdays = new List<DayOfWeek>
        {
            DayOfWeek.Monday,
            DayOfWeek.Tuesday,
            DayOfWeek.Wednesday,
            DayOfWeek.Thursday,
            DayOfWeek.Friday
        };
        var job = new Job
        {
            Name = "weekday",
            Status = JobStatus.Pending,
            ScheduledStart = new DateTimeOffset(new DateTime(2026, 9, 19, 9, 0, 0, DateTimeKind.Local)),
            Options = new JobOptions { ScheduleDays = weekdays }
        };
        var later = new Job { Name = "later", Status = JobStatus.Pending };

        Assert.False(JobDue.IsDue(job, saturday));
        Assert.Equal("Waiting for schedule", JobDue.StatusLabel(job, saturday));
        Assert.Null(JobDue.FindNext([job, later], saturday));

        var mondayEarly = saturday.AddDays(2).AddHours(-2);
        Assert.Equal(DayOfWeek.Monday, mondayEarly.DayOfWeek);
        Assert.False(JobDue.IsDue(job, mondayEarly));
        Assert.StartsWith("Waiting for schedule", JobDue.StatusLabel(job, mondayEarly));

        var mondayNine = new DateTimeOffset(new DateTime(2026, 9, 21, 9, 0, 0, DateTimeKind.Local));
        Assert.True(JobDue.IsDue(job, mondayNine));
        Assert.Same(job, JobDue.FindNext([job, later], mondayNine));
        Assert.Contains("Schedule weekdays", JobOptionBadges.For(job));
        Assert.DoesNotContain(JobOptionBadges.For(job), b => b.Contains("Start After", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingScheduleDaysMeansEveryDay()
    {
        var sunday = new DateTimeOffset(new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Local));
        Assert.Equal(DayOfWeek.Sunday, sunday.DayOfWeek);
        var job = new Job { Status = JobStatus.Pending, Options = new JobOptions() };
        Assert.Null(job.Options.ScheduleDays);
        Assert.True(JobDue.IsDue(job, sunday));
        Assert.Null(ScheduleWeek.Chip(job.Options));
        Assert.True(job.Options.FixLongOrDuplicateNames);
        Assert.DoesNotContain("Don't fix names", JobOptionBadges.For(job));
        var off = job.Options.Clone();
        off.FixLongOrDuplicateNames = false;
        Assert.Contains("Don't fix names", JobOptionBadges.For(new Job { Options = off }));
    }

    [Fact]
    public void ScheduleDaysRoundTripThroughQueueStore()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-days-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            QueueStore.Save(paths,
            [
                new Job
                {
                    Name = "mwf",
                    SourcePath = Path.Combine(root, "src"),
                    DestinationPath = Path.Combine(root, "dest"),
                    Options = new JobOptions
                    {
                        ScheduleDays = [DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday],
                        FixLongOrDuplicateNames = false
                    }
                }
            ]);

            var loaded = QueueStore.Load(paths);
            Assert.Equal(
                [DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday],
                loaded[0].Options.ScheduleDays);
            Assert.False(loaded[0].Options.FixLongOrDuplicateNames);
            Assert.Equal("Schedule Mon Wed Fri", ScheduleWeek.Chip(loaded[0].Options));
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
                // temp leftover
            }
        }
    }

    [Fact]
    public void QueueResumeClearsTransferDraft()
    {
        var draft = new TransferDraft
        {
            SourcePath = Path.Combine(Path.GetTempPath(), "src"),
            SourcePaths = [Path.Combine(Path.GetTempPath(), "src"), Path.Combine(Path.GetTempPath(), "other")],
            DestinationPath = Path.Combine(Path.GetTempPath(), "dest"),
            DestinationKind = 1,
            ScheduleEnabled = true,
            ScheduledDate = new DateTime(2026, 9, 19),
            ScheduledTime = "18:30",
            Options = new JobOptions
            {
                DryRun = true,
                HoursEnabled = true,
                FixLongOrDuplicateNames = false,
                ScheduleDays = [DayOfWeek.Monday]
            }
        };

        draft.ClearForQueueResume();

        Assert.True(draft.IsCleared);
        Assert.Equal("", draft.SourcePath);
        Assert.Empty(draft.SourcePaths);
        Assert.Equal("", draft.DestinationPath);
        Assert.Equal(0, draft.DestinationKind);
        Assert.False(draft.ScheduleEnabled);
        Assert.Equal("09:00", draft.ScheduledTime);
        Assert.False(draft.Options.DryRun);
        Assert.True(draft.Options.FixLongOrDuplicateNames);
        Assert.Null(draft.Options.ScheduleDays);
    }

    private static string NameOf(string path)
    {
        var slash = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
        return slash < 0 ? path : path[(slash + 1)..];
    }

    private static FileRecord FileAt(string destDir, string name, string? source = null) => new()
    {
        RelativePath = name,
        DestPath = destDir + "\\" + name,
        SourcePath = source ?? @"D:\src\" + name,
        Status = FileCopyStatus.Pending
    };
}
