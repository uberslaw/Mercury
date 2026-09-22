namespace Mercury.Tests;

public class JobSourcesTests
{
    [Fact]
    public void RootsFallBackToSourcePath()
    {
        var job = new Job { SourcePath = @"D:\photos" };
        Assert.Equal(@"D:\photos", Assert.Single(JobSources.Roots(job)));
    }

    [Fact]
    public void SetDedupesAndKeepsSourcePathFirst()
    {
        var job = new Job();
        JobSources.Set(job, [@"C:\a", @"C:\a\", @"C:\b"]);
        Assert.Equal(2, job.SourcePaths.Count);
        Assert.Equal(job.SourcePaths[0], job.SourcePath, ignoreCase: true);
        Assert.Contains(" + 1 more", JobSources.Display(job), StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyListClearsSourcePath()
    {
        var job = new Job { SourcePath = @"D:\photos" };
        JobSources.Set(job, []);
        Assert.Empty(JobSources.Roots(job));
        Assert.Equal("", job.SourcePath);
        Assert.Empty(job.SourcePaths);
    }

    [Fact]
    public void SameIgnoresOrder()
    {
        var job = new Job();
        JobSources.Set(job, [@"D:\one", @"D:\two"]);
        Assert.True(JobSources.Same(job, [@"D:\two", @"D:\one"]));
        Assert.False(JobSources.Same(job, [@"D:\one"]));
    }
}

public class CopyShapeMultiSourceTests
{
    [Fact]
    public void TwoFoldersKeepSeparateLandingsAndDisambiguateCollision()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-multi-" + Guid.NewGuid().ToString("N"));
        var dest = Path.Combine(root, "dest");
        var photosA = Path.Combine(root, "left", "Photos");
        var photosB = Path.Combine(root, "right", "Photos");
        Directory.CreateDirectory(photosA);
        Directory.CreateDirectory(photosB);
        Directory.CreateDirectory(dest);
        try
        {
            var mappings = CopyShape.ResolveAll([photosA, photosB], dest, includeSourceFolderName: true);
            Assert.Equal(2, mappings.Count);
            Assert.Equal(Path.Combine(dest, "Photos"), mappings[0].DestRoot, ignoreCase: true);
            Assert.Equal(Path.Combine(dest, "Photos (2)"), mappings[1].DestRoot, ignoreCase: true);
            Assert.Equal("Photos", mappings[0].UniqueRelativePrefix);
            Assert.Equal("Photos (2)", mappings[1].UniqueRelativePrefix);

            var contents = CopyShape.ResolveAll([photosA, photosB], dest, includeSourceFolderName: false);
            Assert.False(string.Equals(contents[0].DestRoot, dest, StringComparison.OrdinalIgnoreCase));
            Assert.Contains("Photos", CopyShape.PreviewLandingSummary([photosA, photosB], dest));
            Assert.Contains("do not smash", CopyShape.PreviewLandingSummary([photosA, photosB], dest, includeSourceFolderName: false), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* leftover */ }
        }
    }

    [Fact]
    public void IncludeOffWithTwoSameFolderNamesStillWrapsAndDisambiguates()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-multi-off-" + Guid.NewGuid().ToString("N"));
        var dest = Path.Combine(root, "dest");
        var photosA = Path.Combine(root, "left", "Photos");
        var photosB = Path.Combine(root, "right", "Photos");
        Directory.CreateDirectory(photosA);
        Directory.CreateDirectory(photosB);
        Directory.CreateDirectory(dest);
        try
        {
            var mappings = CopyShape.ResolveAll([photosA, photosB], dest, includeSourceFolderName: false);
            Assert.Equal(2, mappings.Count);
            Assert.Equal(Path.Combine(dest, "Photos"), mappings[0].DestRoot, ignoreCase: true);
            Assert.Equal(Path.Combine(dest, "Photos (2)"), mappings[1].DestRoot, ignoreCase: true);
            Assert.Equal("Photos", mappings[0].UniqueRelativePrefix);
            Assert.Equal("Photos (2)", mappings[1].UniqueRelativePrefix);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* leftover */ }
        }
    }

    [Fact]
    public void ResolveAllRejectsEmptySourceList()
    {
        var ex = Assert.Throws<ArgumentException>(() => CopyShape.ResolveAll([], @"Z:\dest"));
        Assert.Contains("source", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UniqueRelativeJoinsWithoutChangingDestPath()
    {
        var record = new FileRecord
        {
            RelativePath = @"sub\a.txt",
            DestPath = @"Z:\dest\Photos\sub\a.txt"
        };
        var mapping = new CopyMapping
        {
            DestRoot = @"Z:\dest\Photos",
            UniqueRelativePrefix = "Photos"
        };
        CopyShape.WithUniqueRelative(record, mapping);
        Assert.Equal(@"Photos\sub\a.txt", record.RelativePath);
        Assert.Equal(@"Z:\dest\Photos\sub\a.txt", record.DestPath);
    }
}

public class PackPolicyTests
{
    [Fact]
    public void NormalizeAcceptsDotOrBareExtension()
    {
        Assert.Equal(".mp4", PackPolicy.NormalizeExtension("MP4"));
        Assert.Equal(".mp4", PackPolicy.NormalizeExtension(".Mp4"));
        Assert.Equal(".mp4", PackPolicy.NormalizeExtension("*.mp4"));
        Assert.Equal("", PackPolicy.NormalizeExtension("   "));
    }

    [Fact]
    public void PackListWinsOverCompressedSkip()
    {
        var options = new JobOptions
        {
            SkipCompressedWhenPacking = true,
            NeverPackExtensions = PackPolicy.DefaultNeverPackExtensions(),
            PackExtensions = [".mp4"]
        };
        var file = new FileRecord { SourcePath = @"D:\clip.mp4", RelativePath = "clip.mp4", Size = 100 };
        Assert.False(PackPolicy.ShouldSkipPacking(file, options));
    }

    [Fact]
    public void RemovingDefaultLetsCompressedTypePack()
    {
        var never = PackPolicy.DefaultNeverPackExtensions();
        never.RemoveAll(e => e.Equals(".jpg", StringComparison.OrdinalIgnoreCase));
        var options = new JobOptions
        {
            SkipCompressedWhenPacking = true,
            NeverPackExtensions = never
        };
        var file = new FileRecord { SourcePath = @"D:\pic.jpg", RelativePath = "pic.jpg", Size = 20 };
        Assert.False(PackPolicy.ShouldSkipPacking(file, options));
        Assert.True(PackPolicy.ShouldSkipPacking(
            new FileRecord { SourcePath = @"D:\clip.mp4", RelativePath = "clip.mp4" }, options));
    }

    [Fact]
    public void EmptyNeverPackUsesClassifier()
    {
        var options = new JobOptions { SkipCompressedWhenPacking = true };
        var mkv = new FileRecord { SourcePath = @"D:\a.mkv", RelativePath = "a.mkv" };
        var txt = new FileRecord { SourcePath = @"D:\a.txt", RelativePath = "a.txt" };
        Assert.True(PackPolicy.ShouldSkipPacking(mkv, options));
        Assert.False(PackPolicy.ShouldSkipPacking(txt, options));
    }

    [Fact]
    public void ApplySettingsCopiesNormalizedListsOntoJob()
    {
        var options = new JobOptions { PackExtensions = [".old"], NeverPackExtensions = [".zzz"] };
        PackPolicy.ApplySettings(options, new BandwidthSettings
        {
            PackExtensions = ["MP4"],
            NeverPackExtensions = ["jpg"]
        });
        Assert.Equal([".mp4"], options.PackExtensions);
        Assert.Equal([".jpg"], options.NeverPackExtensions);
    }
}

public class TransferPlannerTests
{
    [Fact]
    public void CompressedAndLargeFilesStreamWhileTinyPocketPacks()
    {
        var dest = @"Z:\dest\Job";
        var mapping = new CopyMapping { Kind = SourceKind.Folder, SourceRoot = @"D:\src", DestRoot = dest };
        var files = new List<FileRecord>
        {
            File("video.mkv", 80L * 1024 * 1024, dest),
            File("big.bin", 10L * 1024 * 1024, dest)
        };
        for (var i = 0; i < 30; i++)
        {
            files.Add(File($@"tiny\n{i}.txt", 1024, dest));
        }

        var plan = TransferPlanner.Build(new Job { Options = new JobOptions() }, files, [mapping]);
        Assert.Contains(plan.Stream, f => f.RelativePath.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Stream, f => f.RelativePath.EndsWith("big.bin", StringComparison.OrdinalIgnoreCase));
        Assert.True(plan.HasPacking);
        Assert.True(plan.PackGroups[0].Files.Count >= 24);
        Assert.Contains("stream", plan.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SmallFilesForcePacksEligibleNotCompressed()
    {
        var dest = @"Z:\dest\Job";
        var mapping = new CopyMapping { Kind = SourceKind.Folder, SourceRoot = @"D:\src", DestRoot = dest };
        var files = new[]
        {
            File("a.txt", 100, dest),
            File("clip.mp4", 5000, dest)
        };
        var plan = TransferPlanner.Build(
            new Job { Options = new JobOptions { PackAsZip = true, SkipCompressedWhenPacking = true } },
            files,
            [mapping]);
        Assert.Contains(plan.Stream, f => f.RelativePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.PackGroups.SelectMany(g => g.Files), f => f.RelativePath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UncertainPocketIsSampledThenCanMoveToStream()
    {
        var dest = @"Z:\dest\Job";
        var mapping = new CopyMapping { Kind = SourceKind.Folder, SourceRoot = @"D:\src", DestRoot = dest };
        var files = new List<FileRecord>();
        for (var i = 0; i < 10; i++)
        {
            files.Add(File($@"mix\n{i}.txt", 100 * 1024, dest));
        }

        var plan = TransferPlanner.Build(new Job { Options = new JobOptions() }, files, [mapping]);
        var pocket = Assert.Single(plan.PackGroups.SelectMany(g => g.Pockets));
        Assert.Equal(PackDisposition.Sample, pocket.Disposition);
        Assert.Contains("256", plan.Summary, StringComparison.Ordinal);

        TransferPlanner.ApplySample(plan, pocket, new PackSampleResult { Pack = false, Reason = "no benefit — copy files as-is" });
        Assert.Equal(PackDisposition.Stream, pocket.Disposition);
        Assert.Equal(10, plan.Stream.Count);
        Assert.False(plan.HasPacking);
    }

    [Fact]
    public void PackListAndNeverPackSplitStreamFromPockets()
    {
        var dest = @"Z:\dest\Job";
        var mapping = new CopyMapping { Kind = SourceKind.Folder, SourceRoot = @"D:\src", DestRoot = dest };
        var files = new List<FileRecord>
        {
            File("clip.mkv", 80L * 1024 * 1024, dest)
        };
        for (var i = 0; i < 30; i++)
        {
            files.Add(File($@"tiny\n{i}.mp4", 1024, dest));
        }

        var options = new JobOptions
        {
            SkipCompressedWhenPacking = true,
            NeverPackExtensions = PackPolicy.DefaultNeverPackExtensions(),
            PackExtensions = [".mp4"]
        };
        var plan = TransferPlanner.Build(new Job { Options = options }, files, [mapping]);
        Assert.Contains(plan.Stream, f => f.RelativePath.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.PackGroups.SelectMany(g => g.Files), f => f.RelativePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.Stream, f => f.RelativePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NeverPackTinyTextStreamsInsteadOfPocket()
    {
        var dest = @"Z:\dest\Job";
        var mapping = new CopyMapping { Kind = SourceKind.Folder, SourceRoot = @"D:\src", DestRoot = dest };
        var files = new List<FileRecord>();
        for (var i = 0; i < 30; i++)
        {
            files.Add(File($@"tiny\n{i}.txt", 1024, dest));
        }

        var plan = TransferPlanner.Build(
            new Job { Options = new JobOptions { NeverPackExtensions = [".txt"] } },
            files,
            [mapping]);
        Assert.False(plan.HasPacking);
        Assert.Equal(30, plan.Stream.Count);
    }

    [Fact]
    public void CatcherPacksEveryFileIntoOneGroup()
    {
        var dest = @"Z:\dest\Job";
        var mapping = new CopyMapping { Kind = SourceKind.Folder, SourceRoot = @"D:\src", DestRoot = dest };
        var files = new[]
        {
            File("video.mkv", 80L * 1024 * 1024, dest),
            File("a.txt", 10, dest)
        };
        var plan = TransferPlanner.Build(
            new Job { Options = new JobOptions(), Catcher = new CatcherTarget { PublicHost = "catcher.example" } },
            files,
            [mapping]);
        var group = Assert.Single(plan.PackGroups);
        Assert.Equal(2, group.Files.Count);
        Assert.Empty(plan.Stream);
        Assert.Contains("Catcher", plan.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OverlapReadyPlanHasStreamAndPackGroups()
    {
        var dest = @"Z:\dest\Job";
        var mapping = new CopyMapping { Kind = SourceKind.Folder, SourceRoot = @"D:\src", DestRoot = dest };
        var files = new List<FileRecord>
        {
            File("video.mkv", 80L * 1024 * 1024, dest)
        };
        for (var i = 0; i < 30; i++)
        {
            files.Add(File($@"tiny\n{i}.txt", 1024, dest));
        }

        var plan = TransferPlanner.Build(new Job { Options = new JobOptions() }, files, [mapping]);
        Assert.True(plan.Stream.Count > 0 && plan.HasPacking);
        var stages = CopyPipeline.For(new Job(), hasJournalFiles: true, pack: true, overlap: true);
        Assert.Contains(stages, s => s.Label.Contains("Copying and packing", StringComparison.Ordinal));
    }

    private static FileRecord File(string relative, long size, string destRoot) =>
        new()
        {
            RelativePath = relative,
            SourcePath = Path.Combine(@"D:\src", relative),
            DestPath = Path.Combine(destRoot, relative),
            Size = size,
            Status = FileCopyStatus.Pending
        };
}

public class PackSampleTests
{
    [Fact]
    public void ManyTinyTextFilesRecommendStoredZip()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-sample-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var payload = new byte[200];
            Random.Shared.NextBytes(payload);
            var files = new List<FileRecord>();
            for (var i = 0; i < 40; i++)
            {
                var path = Path.Combine(root, $"n{i}.bin");
                System.IO.File.WriteAllBytes(path, payload);
                files.Add(new FileRecord
                {
                    RelativePath = Path.GetFileName(path),
                    SourcePath = path,
                    DestPath = Path.Combine(root, "dest", Path.GetFileName(path)),
                    Size = payload.Length
                });
            }

            var result = PackSample.Evaluate(files, sampleBytes: PackSample.MinSampleBytes);
            Assert.True(result.Pack);
            Assert.False(result.Deflate);
            Assert.Contains("stored", result.Reason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* leftover */ }
        }
    }

    [Fact]
    public void FewLargeFilesSkipPacking()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-sample2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var payload = new byte[200_000];
            Random.Shared.NextBytes(payload);
            var files = new List<FileRecord>();
            for (var i = 0; i < 3; i++)
            {
                var path = Path.Combine(root, $"b{i}.bin");
                System.IO.File.WriteAllBytes(path, payload);
                files.Add(new FileRecord
                {
                    RelativePath = Path.GetFileName(path),
                    SourcePath = path,
                    Size = payload.Length
                });
            }

            var result = PackSample.Evaluate(files, sampleBytes: PackSample.MinSampleBytes);
            Assert.False(result.Pack);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* leftover */ }
        }
    }

    [Fact]
    public void SampleBytesStayIn100To500MbClass()
    {
        Assert.Equal(100L * 1024 * 1024, PackSample.ClampSampleBytes(1, 10L * 1024 * 1024 * 1024));
        Assert.Equal(500L * 1024 * 1024, PackSample.ClampSampleBytes(9L * 1024 * 1024 * 1024, 9L * 1024 * 1024 * 1024));
        Assert.Equal(50L * 1024 * 1024, PackSample.ClampSampleBytes(256L * 1024 * 1024, 50L * 1024 * 1024));
    }
}

public class PackSettingsStoreTests
{
    [Fact]
    public void SettingsRoundtripPackExtensionLists()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-packset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            AppSettingsStore.Save(paths, new BandwidthSettings
            {
                PackExtensions = ["MP4", ".txt"],
                NeverPackExtensions = ["jpg", ".zip"]
            });
            var loaded = AppSettingsStore.Load(paths);
            Assert.Contains(".mp4", loaded.PackExtensions);
            Assert.Contains(".txt", loaded.PackExtensions);
            Assert.NotNull(loaded.NeverPackExtensions);
            Assert.Contains(".jpg", loaded.NeverPackExtensions);
            Assert.Contains(".zip", loaded.NeverPackExtensions);
            var json = System.IO.File.ReadAllText(paths.SettingsFile);
            Assert.Contains("packExtensions", json, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* leftover */ }
        }
    }
}

public class QueueMultiSourceTests
{
    [Fact]
    public void QueueStoreRoundtripsSourcePaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-qsrc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            QueueStore.Save(paths,
            [
                new Job
                {
                    SourcePath = @"D:\photos",
                    SourcePaths = [@"D:\photos", @"E:\videos"],
                    DestinationPath = @"Z:\backup"
                }
            ]);
            var loaded = QueueStore.Load(paths);
            Assert.Equal(2, loaded[0].SourcePaths.Count);
            Assert.Contains(@"E:\videos", loaded[0].SourcePaths);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* leftover */ }
        }
    }
}

public class JournalAndLibraryMultiSourceTests
{
    [Fact]
    public void JobJournalRoundtripsSourcePathsAndPackLists()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-jsrc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var job = new Job
            {
                Id = "job-src",
                SourcePath = @"D:\photos",
                SourcePaths = [@"D:\photos", @"E:\videos"],
                DestinationPath = @"Z:\backup",
                Options = new JobOptions
                {
                    PackExtensions = [".mp4"],
                    NeverPackExtensions = [".zip"]
                }
            };
            using (var journal = JobJournal.Create(root, job))
            {
                journal.SaveJob(job);
            }

            using var opened = JobJournal.Open(root);
            var loaded = opened.LoadJob();
            Assert.Equal(2, loaded.SourcePaths.Count);
            Assert.Contains(@"E:\videos", loaded.SourcePaths);
            Assert.Contains(".mp4", loaded.Options.PackExtensions);
            Assert.Contains(".zip", loaded.Options.NeverPackExtensions);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* leftover */ }
        }
    }

    [Fact]
    public void LibraryStoreRoundtripsSavedJobSourcePaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-savedsrc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            LibraryStore.SaveSavedJobs(paths,
            [
                new SavedJob
                {
                    Name = "weekend",
                    SourcePath = @"D:\photos",
                    SourcePaths = [@"D:\photos", @"E:\videos"],
                    DestinationPath = @"Z:\backup"
                }
            ]);
            var loaded = LibraryStore.LoadSavedJobs(paths);
            Assert.Equal(2, loaded[0].SourcePaths.Count);
            Assert.Contains(@"E:\videos", loaded[0].SourcePaths);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* leftover */ }
        }
    }
}
