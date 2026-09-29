using System.Text;

namespace Mercury.Tests;

public class FileClassifierTests
{
    [Fact]
    public void ExtensionClassifiesMediaAndDiskImages()
    {
        Assert.Equal(PayloadKind.Video, FileClassifier.FromExtension(@"D:\clip.mkv"));
        Assert.Equal(PayloadKind.Audio, FileClassifier.FromExtension("song.mp3"));
        Assert.Equal(PayloadKind.Image, FileClassifier.FromExtension("photo.JPG"));
        Assert.Equal(PayloadKind.Archive, FileClassifier.FromExtension("pack.zip"));
        Assert.Equal(PayloadKind.DiskImage, FileClassifier.FromExtension("win.iso"));
        Assert.Equal(PayloadKind.DiskImage, FileClassifier.FromExtension("disk.vhd"));
        Assert.Equal(PayloadKind.DiskImage, FileClassifier.FromExtension("disk.vhdx"));
        Assert.Equal(PayloadKind.DiskImage, FileClassifier.FromExtension("raw.img"));
        Assert.Equal(PayloadKind.Other, FileClassifier.FromExtension("notes.txt"));
        Assert.Equal(PayloadKind.Other, FileClassifier.FromExtension("slide.docx"));
        Assert.Contains(".mkv", FileClassifier.AllCompressedExtensions());
        Assert.Contains(".jpg", FileClassifier.AllCompressedExtensions());
        Assert.True(FileClassifier.IsCompressedExtension("mp4"));
    }

    [Fact]
    public void MagicUpgradesUnknownExtension()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mercury-magic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var png = Path.Combine(dir, "noext.bin");
            var bytes = new byte[2 * 1024 * 1024];
            "PNG".CopyTo(0, new char[3], 0, 0);
            bytes[0] = 0x89;
            bytes[1] = (byte)'P';
            bytes[2] = (byte)'N';
            bytes[3] = (byte)'G';
            File.WriteAllBytes(png, bytes);
            var peek = new MagicPeekBudget();
            Assert.Equal(PayloadKind.Image, FileClassifier.Classify(png, bytes.Length, peek));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* leftover */ }
        }
    }

    [Fact]
    public void InventoryFormatsTypeSummary()
    {
        var inventory = new PayloadInventory();
        inventory.Add(PayloadKind.Video, 2L * 1024 * 1024 * 1024);
        inventory.Add(PayloadKind.Video, 100);
        inventory.Add(PayloadKind.Other, 50);
        var summary = inventory.FormatSummary();
        Assert.Contains("Video: 2 files", summary, StringComparison.Ordinal);
        Assert.Contains("Other: 1 files", summary, StringComparison.Ordinal);
        Assert.Contains("(100%)", summary, StringComparison.Ordinal);
        Assert.Contains("(<1%)", summary, StringComparison.Ordinal);
        Assert.True(inventory.ShouldProbeUnbuffered(UnbufferedIoPolicy.Default));
    }

    [Fact]
    public void InventoryFormatsByteShareNotFileShare()
    {
        var inventory = new PayloadInventory();
        inventory.Add(PayloadKind.Video, 82);
        for (var i = 1; i < 12632; i++)
        {
            inventory.Add(PayloadKind.Video, 0);
        }

        inventory.Add(PayloadKind.Image, 1);
        for (var i = 1; i < 126; i++)
        {
            inventory.Add(PayloadKind.Image, 0);
        }

        inventory.Add(PayloadKind.Archive, 17);
        for (var i = 1; i < 515; i++)
        {
            inventory.Add(PayloadKind.Archive, 0);
        }

        var summary = inventory.FormatSummary();
        Assert.Equal(
            "Video: 12,632 files, 82 B (82%); Images: 126 files, 1 B (1%); Archives: 515 files, 17 B (17%)",
            summary);
    }

    [Fact]
    public void ArchiveAndAudioCountAsSequentialForProbe()
    {
        var archives = new PayloadInventory();
        archives.Add(PayloadKind.Archive, 80L * 1024 * 1024);
        archives.Add(PayloadKind.Archive, 80L * 1024 * 1024);
        archives.Add(PayloadKind.Other, 8L * 1024 * 1024);
        Assert.True(archives.SequentialDominates);
        Assert.True(archives.ShouldProbeUnbuffered(UnbufferedIoPolicy.Default));

        var audio = new PayloadInventory();
        audio.Add(PayloadKind.Audio, 90L * 1024 * 1024);
        audio.Add(PayloadKind.Audio, 90L * 1024 * 1024);
        audio.Add(PayloadKind.Image, 2L * 1024 * 1024);
        Assert.True(audio.SequentialDominates);
        Assert.True(audio.ShouldProbeUnbuffered(UnbufferedIoPolicy.Default));
    }
}

public class UnbufferedIoTests
{
    [Fact]
    public void ForceOnSkipsProbe()
    {
        var inventory = LargeVideo();
        var session = new UnbufferedIoSession(new JobOptions { UnbufferedIo = true }, inventory);
        Assert.Equal("forced", session.SkipReason);
        Assert.True(session.UseUnbufferedFor("clip.mkv", 1024));
        Assert.False(session.NeedsBufferedSample("clip.mkv", inventory.LargestFileBytes));
    }

    [Fact]
    public void TinyTreeSkipsProbe()
    {
        var inventory = new PayloadInventory();
        for (var i = 0; i < 100; i++)
        {
            inventory.Add(PayloadKind.Other, 4096);
        }

        var session = new UnbufferedIoSession(new JobOptions(), inventory);
        Assert.Contains("no large sequential", session.SkipReason, StringComparison.OrdinalIgnoreCase);
        Assert.False(session.UseUnbufferedFor("a.txt", 4096));
    }

    [Fact]
    public void MixedTreeKeepsSmallFilesBufferedAfterWin()
    {
        var policy = new UnbufferedIoPolicy
        {
            LargeFileBytes = 1000,
            SequentialApplyBytes = 500,
            ProbeSampleBytes = 400,
            MinProbeSampleBytes = 100
        };
        var inventory = new PayloadInventory();
        inventory.Add(PayloadKind.Video, 8000);
        inventory.Add(PayloadKind.Other, 20);
        var session = new UnbufferedIoSession(new JobOptions(), inventory, policy);
        session.NoteBuffered(10);
        session.Complete(10, 20);
        Assert.True(session.UseUnbufferedFor("movie.mkv", 8000));
        Assert.False(session.UseUnbufferedFor("readme.txt", 20));
    }

    [Fact]
    public void IdleThrottleSkipsProbe()
    {
        var budget = new BandwidthBudget
        {
            IdleThrottleBytesPerSecond = 2 * 1024 * 1024,
            MachineLoad = new FixedMachineLoad { IsBusy = true }
        };
        var session = UnbufferedIoSession.Create(new JobOptions(), LargeVideo(), budget);
        Assert.Contains("idle throttle", session.SkipReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProbeLineNamesWinner()
    {
        var line = UnbufferedIoSession.FormatProbeLine(85 * 1024 * 1024, 110 * 1024 * 1024, useUnbuffered: true);
        Assert.Contains("buffered 85", line, StringComparison.Ordinal);
        Assert.Contains("unbuffered 110", line, StringComparison.Ordinal);
        Assert.Contains("using unbuffered", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForceUnbufferedCopyKeepsUnalignedSize()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-unbuf-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(root, "src");
        var dest = Path.Combine(root, "dest");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);
        var payload = Encoding.UTF8.GetBytes(new string('x', 1000));
        var file = Path.Combine(src, "odd.bin");
        File.WriteAllBytes(file, payload);
        try
        {
            var job = new Job
            {
                Name = "unbuf",
                SourcePath = src,
                DestinationPath = dest,
                Options = new JobOptions { UnbufferedIo = true, RetryCount = 1, Verify = VerifyLevel.Thorough }
            };
            var record = new FileRecord
            {
                RelativePath = "odd.bin",
                SourcePath = file,
                DestPath = Path.Combine(dest, "odd.bin"),
                Size = payload.Length
            };
            var hash = await CopyEngine.CopyOneAsync(
                job, record, new BandwidthBudget(), new PauseGate(), new SpeedTracker(), CancellationToken.None);
            Assert.True(File.Exists(record.DestPath));
            Assert.Equal(payload, File.ReadAllBytes(record.DestPath));
            Assert.False(string.IsNullOrEmpty(hash));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* leftover */ }
        }
    }

    private static PayloadInventory LargeVideo()
    {
        var inventory = new PayloadInventory();
        inventory.Add(PayloadKind.Video, 300L * 1024 * 1024);
        return inventory;
    }
}

public class JobHeartbeatTests
{
    [Fact]
    public void DirtySidecarSurvivesWithoutClear()
    {
        var data = Path.Combine(Path.GetTempPath(), "mercury-hb-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(data);
        var src = Path.Combine(data, "Warren Truss");
        var job = new Job { Name = "hb", SourcePath = src, DestinationPath = Path.Combine(data, "dest") };
        using (var journal = JobJournal.Create(paths.JobDirectory(job.Id), job))
        {
            JobHeartbeat.Write(journal, job, 42.4, "clip.mkv");
            var read = JobHeartbeat.Read(journal, job.Id);
            Assert.NotNull(read);
            Assert.True(read!.Dirty);
            Assert.Equal("clip.mkv", read.File);
            Assert.InRange(read.Percent, 42, 43);
        }

        var found = JobHeartbeat.FindDirty(paths);
        Assert.NotNull(found);
        Assert.Equal(job.Id, found!.JobId);
        var message = JobHeartbeat.UnscheduledStopMessage(found, job);
        Assert.Contains("Unscheduled stop of", message, StringComparison.Ordinal);
        Assert.Contains(src, message, StringComparison.Ordinal);
        Assert.Contains("42%", message, StringComparison.Ordinal);

        using (var journal = JobJournal.Open(paths.JobDirectory(job.Id)))
        {
            JobHeartbeat.Clear(journal);
        }

        Assert.Null(JobHeartbeat.FindDirty(paths));
        try { Directory.Delete(data, true); } catch { /* leftover */ }
    }

    [Fact]
    public void DecliningStaleZeroPercentHeartbeatDoesNotReappear()
    {
        var data = Path.Combine(Path.GetTempPath(), "mercury-hb-zero-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(data);
        var src = Path.Combine(data, "Anchor Span");
        var dest = Path.Combine(data, "EngA");
        var leftover = new Job
        {
            Name = "leftover",
            SourcePath = src,
            DestinationPath = dest,
            Status = JobStatus.Incomplete,
            SourceFiles = 0,
            DestFiles = 0,
            BytesCopied = 0
        };
        var catchUp = new Job
        {
            Name = "catch-up",
            SourcePath = Path.Combine(data, "Warren Truss"),
            DestinationPath = dest,
            Status = JobStatus.Incomplete,
            SourceFiles = 26000,
            DestFiles = 23000,
            BytesCopied = 1
        };
        try
        {
            using (var journal = JobJournal.Create(paths.JobDirectory(leftover.Id), leftover))
            {
                JobHeartbeat.Write(journal, leftover, 0, null);
            }

            var dirty = JobHeartbeat.FindDirty(paths);
            Assert.NotNull(dirty);
            Assert.Equal(leftover.Id, dirty!.JobId);
            Assert.Contains(src, JobHeartbeat.UnscheduledStopMessage(dirty, leftover), StringComparison.Ordinal);
            Assert.Equal(DirtyResumeDecision.SkipClear, JobHeartbeat.Decide(dirty, leftover, [leftover, catchUp]));

            var scheduler = new JobScheduler(paths);
            try
            {
                scheduler.ClearDirtyHeartbeat(leftover.Id);
            }
            finally
            {
                scheduler.Dispose();
            }

            Assert.Null(JobHeartbeat.FindDirty(paths));
            Assert.False(File.Exists(JobHeartbeat.SidecarPath(paths.JobDirectory(leftover.Id))));
        }
        finally
        {
            try { Directory.Delete(data, true); } catch { /* leftover */ }
        }
    }

    [Fact]
    public void ClearSidecarWorksWithoutJournal()
    {
        var data = Path.Combine(Path.GetTempPath(), "mercury-hb-orphan-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(data);
        var dir = paths.JobDirectory("orphan");
        Directory.CreateDirectory(dir);
        File.WriteAllText(JobHeartbeat.SidecarPath(dir), """{"jobId":"orphan","dirty":true,"percent":0,"utc":"2026-09-21T07:03:00Z"}""");
        Assert.NotNull(JobHeartbeat.FindDirty(paths));
        JobHeartbeat.ClearSidecar(dir, "orphan");
        Assert.Null(JobHeartbeat.FindDirty(paths));
        try { Directory.Delete(data, true); } catch { /* leftover */ }
    }

    [Fact]
    public void FreshPulseIsKeptNotOffered()
    {
        var dirty = JobHeartbeat.Create("live", 88, "clip.mkv", @"D:\Warren Truss", @"Z:\dest");
        var job = new Job
        {
            Id = "live",
            SourcePath = @"D:\Warren Truss",
            DestinationPath = @"Z:\dest",
            Status = JobStatus.Copying,
            BytesCopied = 10,
            SourceFiles = 100,
            DestFiles = 80
        };
        Assert.Equal(DirtyResumeDecision.SkipKeep, JobHeartbeat.Decide(dirty, job, [job]));
    }

    [Fact]
    public void OtherQueueJobSuppressesStaleHeartbeat()
    {
        var dirty = new JobHeartbeatState
        {
            JobId = "old",
            Dirty = true,
            Percent = 0,
            Utc = DateTimeOffset.UtcNow.AddDays(-8)
        };
        var stale = new Job { Id = "old", Status = JobStatus.Cancelled, SourcePath = @"D:\old" };
        var real = new Job
        {
            Id = "catch",
            Status = JobStatus.Incomplete,
            SourcePath = @"D:\Warren Truss",
            SourceFiles = 20000,
            DestFiles = 18000,
            BytesCopied = 50
        };
        Assert.Equal(DirtyResumeDecision.SkipClear, JobHeartbeat.Decide(dirty, stale, [stale, real]));
    }

    [Fact]
    public void CloseCopyMentionsWaitAndCloseNow()
    {
        var longWait = JobHeartbeat.CloseWhileRunningMessage(TimeSpan.FromMinutes(12));
        Assert.Contains("Wait", longWait, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("close right now", longWait, StringComparison.OrdinalIgnoreCase);
        var shortWait = JobHeartbeat.CloseWhileRunningMessage(TimeSpan.FromSeconds(4));
        Assert.Contains("few seconds", shortWait, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Wait for this file (~12m)", JobHeartbeat.WaitForThisFileLabel(TimeSpan.FromMinutes(12)));
        Assert.Equal("Wait for this file", JobHeartbeat.WaitForThisFileLabel(TimeSpan.FromSeconds(4)));
        Assert.Equal("Wait for this file", JobHeartbeat.WaitForThisFileLabel(null));
    }
}
