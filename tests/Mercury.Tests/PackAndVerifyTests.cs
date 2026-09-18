using System.IO.Compression;

namespace Mercury.Tests;

public class PackAndVerifyTests
{
    [Fact]
    public async Task PackAsZipCopiesCompressedFilesAsIs()
    {
        var (src, dest, paths) = CreateTrees();
        try
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "alpha");
            File.WriteAllText(Path.Combine(src, "clip.mkv"), "video-bytes");
            var job = NewJob(src, dest, pack: true);
            await RunAsync(job, paths);
            Assert.True(job.Status == JobStatus.Completed, $"status={job.Status} msg={job.ResultMessage}");
            var destFolder = Path.Combine(dest, Path.GetFileName(src));
            Assert.True(File.Exists(Path.Combine(destFolder, "a.txt")));
            Assert.True(File.Exists(Path.Combine(destFolder, "clip.mkv")));
            Assert.Equal("video-bytes", File.ReadAllText(Path.Combine(destFolder, "clip.mkv")));
            var zipPath = Path.Combine(dest, Path.GetFileName(src) + ".zip");
            Assert.False(File.Exists(zipPath));
        }
        finally
        {
            Cleanup(src, dest, paths);
        }
    }

    [Fact]
    public async Task PackAsZipWritesOneArchiveAndVerifies()
    {
        var (src, dest, paths) = CreateTrees();
        try
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "alpha");
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            File.WriteAllText(Path.Combine(src, "sub", "b.bin"), new string('x', 10_000));

            var job = NewJob(src, dest, pack: true);
            await RunAsync(job, paths);

            Assert.True(job.Status == JobStatus.Completed, $"status={job.Status} msg={job.ResultMessage}");
            var zipPath = Path.Combine(dest, Path.GetFileName(src) + ".zip");
            var destFolder = Path.Combine(dest, Path.GetFileName(src));
            Assert.False(File.Exists(zipPath), $"transport zip should be deleted: {zipPath}");
            Assert.False(File.Exists(zipPath + ".mercury.tmp"));
            Assert.True(Directory.Exists(destFolder), $"missing dest folder {destFolder}");
            Assert.True(File.Exists(Path.Combine(destFolder, "a.txt")));
            Assert.True(File.Exists(Path.Combine(destFolder, "sub", "b.bin")));
            Assert.Equal("alpha", File.ReadAllText(Path.Combine(destFolder, "a.txt")));
            Assert.Contains("Unpacked 2 files", job.ResultMessage);

            Assert.Equal(2, job.SourceFiles);
            Assert.Equal(2, job.DestFiles);
            Assert.Equal(1, job.SourceFolders);
            Assert.Equal(1, job.DestFolders);
        }
        finally
        {
            Cleanup(src, dest, paths);
        }
    }

    [Fact]
    public async Task PackAsZipThoroughVerifyHashesEntries()
    {
        var (src, dest, paths) = CreateTrees();
        try
        {
            File.WriteAllText(Path.Combine(src, "note.txt"), "hello zip");
            var job = NewJob(src, dest, pack: true);
            job.Options.Verify = VerifyLevel.Thorough;
            await RunAsync(job, paths);
            Assert.Equal(JobStatus.Completed, job.Status);
        }
        finally
        {
            Cleanup(src, dest, paths);
        }
    }

    [Fact]
    public async Task PackAsZipResumeContinuesUnpackWhenZipPresent()
    {
        var (src, dest, paths) = CreateTrees();
        try
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "alpha");
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            File.WriteAllText(Path.Combine(src, "sub", "b.bin"), "beta");

            var zipPath = Path.Combine(dest, Path.GetFileName(src) + ".zip");
            var destFolder = Path.Combine(dest, Path.GetFileName(src));
            Directory.CreateDirectory(dest);
            System.IO.Compression.ZipFile.CreateFromDirectory(src, zipPath, CompressionLevel.NoCompression, includeBaseDirectory: false);
            File.SetLastWriteTimeUtc(zipPath, DateTime.UtcNow.AddMinutes(1));
            Directory.CreateDirectory(destFolder);
            File.WriteAllText(Path.Combine(destFolder, "a.txt"), "alpha");

            var job = NewJob(src, dest, pack: true);
            await RunAsync(job, paths);

            Assert.True(job.Status == JobStatus.Completed, $"status={job.Status} msg={job.ResultMessage}");
            Assert.False(File.Exists(zipPath), "transport zip should be deleted after unpack");
            Assert.True(File.Exists(Path.Combine(destFolder, "a.txt")));
            Assert.True(File.Exists(Path.Combine(destFolder, "sub", "b.bin")));
            Assert.Equal("beta", File.ReadAllText(Path.Combine(destFolder, "sub", "b.bin")));
        }
        finally
        {
            Cleanup(src, dest, paths);
        }
    }

    [Fact]
    public async Task PackAsZipResumeDoesNotRewriteZipAfterUnpack()
    {
        var (src, dest, paths) = CreateTrees();
        try
        {
            File.WriteAllText(Path.Combine(src, "gone.txt"), "data");
            var job = NewJob(src, dest, pack: true);
            await RunAsync(job, paths);
            Assert.Equal(JobStatus.Completed, job.Status);

            var zipPath = Path.Combine(dest, Path.GetFileName(src) + ".zip");
            var destFile = Path.Combine(dest, Path.GetFileName(src), "gone.txt");
            Assert.False(File.Exists(zipPath));
            Assert.True(File.Exists(destFile));

            job.Status = JobStatus.Pending;
            await RunAsync(job, paths, resume: true);
            Assert.Equal(JobStatus.Completed, job.Status);
            Assert.False(File.Exists(zipPath));
            Assert.True(File.Exists(destFile));
            Assert.Equal("data", File.ReadAllText(destFile));
        }
        finally
        {
            Cleanup(src, dest, paths);
        }
    }

    [Fact]
    public async Task PackReportsUnpackingStage()
    {
        var (src, dest, paths) = CreateTrees();
        try
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "alpha");
            var job = NewJob(src, dest, pack: true);
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

            Assert.Contains("Packing", seen);
            Assert.Contains("Unpacking", seen);
            Assert.Contains("Verifying", seen);
        }
        finally
        {
            Cleanup(src, dest, paths);
        }
    }

    [Fact]
    public async Task ExtractCancelKeepsTransportZip()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-unpack-cancel-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(root, "src");
        var dest = Path.Combine(root, "dest");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(src, "a.txt"), "one");
        File.WriteAllText(Path.Combine(src, "sub", "b.bin"), "two");
        var zipPath = Path.Combine(dest, "src.zip");
        ZipFile.CreateFromDirectory(src, zipPath, CompressionLevel.NoCompression, includeBaseDirectory: false);
        var destRoot = Path.Combine(dest, "src");
        using var cts = new CancellationTokenSource();
        var unpacked = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ZipPack.ExtractAsync(
                zipPath,
                destRoot,
                files: null,
                OverwritePolicy.Always,
                job: null,
                budget: null,
                pause: null,
                speed: null,
                log: null,
                name: "test",
                progress: null,
                cloud: false,
                waitHours: null,
                onUnpacked: _ =>
                {
                    unpacked++;
                    if (unpacked >= 1)
                    {
                        cts.Cancel();
                    }
                },
                cts.Token));

        Assert.True(File.Exists(zipPath), "zip must remain after Stop during unpack");
        Assert.True(unpacked >= 1);
        Assert.False(File.Exists(zipPath + ".mercury.tmp"));
        try
        {
            Directory.Delete(root, true);
        }
        catch
        {
            // temp leftover is OK
        }
    }

    [Fact]
    public async Task SmallFileCopySkipsTempSidecar()
    {
        var (src, dest, paths) = CreateTrees();
        try
        {
            File.WriteAllText(Path.Combine(src, "tiny.txt"), "hi");
            var job = NewJob(src, dest, pack: false);
            await RunAsync(job, paths);
            Assert.Equal(JobStatus.Completed, job.Status);

            var copied = Path.Combine(dest, Path.GetFileName(src), "tiny.txt");
            Assert.True(File.Exists(copied));
            Assert.Equal("hi", File.ReadAllText(copied));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(copied)!, "*.mercury.tmp"));
        }
        finally
        {
            Cleanup(src, dest, paths);
        }
    }

    [Fact]
    public void AlreadyCompressedSkipsVideoArchivesPhotosAudio()
    {
        Assert.True(CompressedMedia.IsAlreadyCompressed(@"D:\clip.mkv"));
        Assert.True(CompressedMedia.IsAlreadyCompressed("photo.JPG"));
        Assert.True(CompressedMedia.IsAlreadyCompressed("song.mp3"));
        Assert.True(CompressedMedia.IsAlreadyCompressed("pack.zip"));
        Assert.True(CompressedMedia.IsAlreadyCompressed("disk.iso"));
        Assert.True(CompressedMedia.IsAlreadyCompressed("drive.vhdx"));
        Assert.False(CompressedMedia.IsAlreadyCompressed("notes.txt"));
        Assert.False(CompressedMedia.IsAlreadyCompressed("slide.docx"));
    }

    [Fact]
    public void ZipPathForFolderIsBesideWouldBeDestFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-zippath-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(root, "Photos");
        var dest = Path.Combine(root, "USB");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(src, "a.txt"), "x");
        try
        {
            var mapping = CopyShape.Resolve(src, dest);
            Assert.Equal(Path.Combine(dest, "Photos.zip"), ZipPack.ZipPath(mapping));
            Assert.True(ZipPack.Applies(new Job { Options = new JobOptions { PackAsZip = true } }, mapping));

            var fileMapping = CopyShape.Resolve(Path.Combine(src, "a.txt"), dest);
            Assert.False(ZipPack.Applies(new Job { Options = new JobOptions { PackAsZip = true } }, fileMapping));
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

    private static (string src, string dest, AppPaths paths) CreateTrees()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-pack-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(root, "src");
        var dest = Path.Combine(root, "dest");
        var data = Path.Combine(root, "app");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);
        Directory.CreateDirectory(data);
        return (src, dest, new AppPaths(data));
    }

    private static Job NewJob(string src, string dest, bool pack) =>
        new()
        {
            Name = "pack-test",
            SourcePath = src,
            DestinationPath = dest,
            SourceKind = SourceKind.Folder,
            Options = new JobOptions
            {
                RetryCount = 1,
                RetryWaitSeconds = 1,
                PackAsZip = pack
            }
        };

    private static async Task RunAsync(Job job, AppPaths paths, bool resume = false)
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
