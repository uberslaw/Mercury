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
        Assert.True(inventory.ShouldProbeUnbuffered(UnbufferedIoPolicy.Default));
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
        var job = new Job { Name = "hb" };
        using (var journal = JobJournal.Create(paths.JobDirectory(job.Id), job))
        {
            JobHeartbeat.Write(journal, job.Id, 42.4, "clip.mkv");
            var read = JobHeartbeat.Read(journal, job.Id);
            Assert.NotNull(read);
            Assert.True(read!.Dirty);
            Assert.Equal("clip.mkv", read.File);
            Assert.InRange(read.Percent, 42, 43);
        }

        var found = JobHeartbeat.FindDirty(paths);
        Assert.NotNull(found);
        Assert.Equal(job.Id, found!.JobId);
        Assert.Contains("Unscheduled stop at 42%", JobHeartbeat.UnscheduledStopMessage(found), StringComparison.Ordinal);

        using (var journal = JobJournal.Open(paths.JobDirectory(job.Id)))
        {
            JobHeartbeat.Clear(journal);
        }

        Assert.Null(JobHeartbeat.FindDirty(paths));
        try { Directory.Delete(data, true); } catch { /* leftover */ }
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
