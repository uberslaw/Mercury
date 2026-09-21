using System.Text.Json;

namespace Mercury.Tests;

public class RoboFlagsTests
{
    [Fact]
    public void JobOptionsDefaultsKeepCreationInfo()
    {
        var options = new JobOptions();
        Assert.True(options.CopyTimestamps);
        Assert.True(options.CopyAttributes);
        Assert.False(options.CopySecurity);
        Assert.False(options.CopyOwner);
        Assert.True(options.CopyDirectoryTimestamps);
        Assert.True(options.CopyEmptyDirectories);
        Assert.True(options.IncludeSourceFolderName);
        Assert.False(options.UnbufferedIo);
        Assert.False(options.CopySymbolicLinksAsLinks);
        Assert.False(options.FatTimestampTolerance);
        Assert.False(options.ExcludeHiddenSystem);
        Assert.False(options.PurgeExtraDestFiles);
    }

    [Fact]
    public void MissingJsonKeepsTimestampDefaults()
    {
        var options = JsonSerializer.Deserialize<JobOptions>("{}")!;
        Assert.True(options.CopyTimestamps);
        Assert.True(options.CopyAttributes);
        Assert.True(options.CopyDirectoryTimestamps);
        Assert.True(options.CopyEmptyDirectories);
        Assert.True(options.IncludeSourceFolderName);
        Assert.False(options.PurgeExtraDestFiles);
    }

    [Fact]
    public void HelpMapsRoboFlagsToRobocopyCousins()
    {
        var section = HelpDocument.Sections.Single(s => s.Id == "roboflags");
        Assert.Contains("/COPY:T", section.Body, StringComparison.Ordinal);
        Assert.Contains("/COPY:A", section.Body, StringComparison.Ordinal);
        Assert.Contains("/COPY:S", section.Body, StringComparison.Ordinal);
        Assert.Contains("/COPY:O", section.Body, StringComparison.Ordinal);
        Assert.Contains("/DCOPY:T", section.Body, StringComparison.Ordinal);
        Assert.Contains("/E", section.Body, StringComparison.Ordinal);
        Assert.Contains("/J", section.Body, StringComparison.Ordinal);
        Assert.Contains("auto-probe", section.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Force unbuffered", section.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/SL", section.Body, StringComparison.Ordinal);
        Assert.Contains("/FFT", section.Body, StringComparison.Ordinal);
        Assert.Contains("/XA:HS", section.Body, StringComparison.Ordinal);
        Assert.Contains("/PURGE", section.Body, StringComparison.Ordinal);
        Assert.Contains("does not run robocopy.exe", section.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(HelpDocument.Search("creation time"), s => s.Id == "roboflags");
    }

    [Fact]
    public void HelpMentionsWillLandInPreview()
    {
        var section = HelpDocument.Sections.Single(s => s.Id == "transfer");
        Assert.Contains("Will land in", section.Body, StringComparison.Ordinal);
        Assert.Contains("dest\\FolderName", section.Body, StringComparison.Ordinal);
        Assert.Contains("Include source folder name is on by default", section.Body, StringComparison.Ordinal);
        Assert.Contains("contents-only", section.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("check the source for changes", section.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bottom of the Progress header", section.Body, StringComparison.Ordinal);
        Assert.Contains("Add to queue and Save job stay at the bottom", section.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void HelpMentionsTreeTab()
    {
        var section = HelpDocument.Sections.Single(s => s.Id == "tree");
        Assert.Contains("Folders only", section.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Files", section.Body, StringComparison.Ordinal);
        Assert.Contains("Subdirs", section.Body, StringComparison.Ordinal);
        Assert.Contains("ETA", section.Body, StringComparison.Ordinal);
        Assert.Contains(HelpDocument.Search("Tree"), s => s.Id == "tree");
    }

    [Fact]
    public void HelpMentionsQueueJobOptionsAndWrap()
    {
        var section = HelpDocument.Sections.Single(s => s.Id == "queue");
        Assert.Contains("Job Options", section.Body, StringComparison.Ordinal);
        Assert.Contains("Contents only", section.Body, StringComparison.Ordinal);
        Assert.Contains("Include source folder name", section.Body, StringComparison.Ordinal);
        Assert.Contains("Resume last", section.Body, StringComparison.Ordinal);
        var options = HelpDocument.Sections.Single(s => s.Id == "options");
        Assert.Contains("Include source folder name (default on)", options.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CopyPreservesCreationAndLastWrite()
    {
        var (src, dest, paths) = CreateTrees();
        try
        {
            var file = Path.Combine(src, "kept.txt");
            File.WriteAllText(file, "payload");
            var created = DateTime.UtcNow.AddDays(-40);
            var written = DateTime.UtcNow.AddDays(-7);
            File.SetCreationTimeUtc(file, created);
            File.SetLastWriteTimeUtc(file, written);
            created = File.GetCreationTimeUtc(file);
            written = File.GetLastWriteTimeUtc(file);

            var job = NewJob(src, dest, paths);
            await RunAsync(job, paths);

            Assert.Equal(JobStatus.Completed, job.Status);
            var copied = Path.Combine(dest, Path.GetFileName(src), "kept.txt");
            Assert.True(File.Exists(copied));
            AssertClose(created, File.GetCreationTimeUtc(copied));
            AssertClose(written, File.GetLastWriteTimeUtc(copied));
        }
        finally
        {
            Cleanup(src, dest, paths);
        }
    }

    [Fact]
    public async Task CopyCreatesEmptyDirectoriesByDefault()
    {
        var (src, dest, paths) = CreateTrees();
        try
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "a");
            Directory.CreateDirectory(Path.Combine(src, "empty"));
            var job = NewJob(src, dest, paths);
            await RunAsync(job, paths);
            Assert.Equal(JobStatus.Completed, job.Status);
            Assert.True(Directory.Exists(Path.Combine(dest, Path.GetFileName(src), "empty")));
        }
        finally
        {
            Cleanup(src, dest, paths);
        }
    }

    [Fact]
    public async Task ExcludeHiddenSkipsHiddenFiles()
    {
        var (src, dest, paths) = CreateTrees();
        try
        {
            File.WriteAllText(Path.Combine(src, "visible.txt"), "ok");
            var hidden = Path.Combine(src, "secret.txt");
            File.WriteAllText(hidden, "nope");
            File.SetAttributes(hidden, FileAttributes.Hidden);

            var job = NewJob(src, dest, paths);
            job.Options.ExcludeHiddenSystem = true;
            await RunAsync(job, paths);

            Assert.Equal(JobStatus.Completed, job.Status);
            var destFolder = Path.Combine(dest, Path.GetFileName(src));
            Assert.True(File.Exists(Path.Combine(destFolder, "visible.txt")));
            Assert.False(File.Exists(Path.Combine(destFolder, "secret.txt")));
        }
        finally
        {
            Cleanup(src, dest, paths);
        }
    }

    [Fact]
    public async Task PackUnpackRestoresCreationTime()
    {
        var (src, dest, paths) = CreateTrees();
        try
        {
            var file = Path.Combine(src, "note.txt");
            File.WriteAllText(file, "zip-me");
            var created = DateTime.UtcNow.AddDays(-12);
            File.SetCreationTimeUtc(file, created);
            created = File.GetCreationTimeUtc(file);

            var job = NewJob(src, dest, paths);
            job.Options.PackAsZip = true;
            await RunAsync(job, paths);

            Assert.True(job.Status == JobStatus.Completed, $"status={job.Status} msg={job.ResultMessage}");
            var copied = Path.Combine(dest, Path.GetFileName(src), "note.txt");
            Assert.True(File.Exists(copied));
            AssertClose(created, File.GetCreationTimeUtc(copied));
        }
        finally
        {
            Cleanup(src, dest, paths);
        }
    }

    [Fact]
    public async Task PurgeRemovesExtraDestFile()
    {
        var (src, dest, paths) = CreateTrees();
        try
        {
            File.WriteAllText(Path.Combine(src, "keep.txt"), "keep");
            var job = NewJob(src, dest, paths);
            await RunAsync(job, paths);
            Assert.Equal(JobStatus.Completed, job.Status);

            var destFolder = Path.Combine(dest, Path.GetFileName(src));
            var extra = Path.Combine(destFolder, "extra.txt");
            File.WriteAllText(extra, "remove me");

            var purge = NewJob(src, dest, paths);
            purge.Options.PurgeExtraDestFiles = true;
            await RunAsync(purge, paths);
            Assert.Equal(JobStatus.Completed, purge.Status);
            Assert.True(File.Exists(Path.Combine(destFolder, "keep.txt")));
            Assert.False(File.Exists(extra));
        }
        finally
        {
            Cleanup(src, dest, paths);
        }
    }

    private static void AssertClose(DateTime expected, DateTime actual)
    {
        var delta = (expected - actual).Duration();
        Assert.True(delta < TimeSpan.FromSeconds(2), $"expected {expected:O}, actual {actual:O}, delta {delta}");
    }

    private static (string src, string dest, AppPaths paths) CreateTrees()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-robo-" + Guid.NewGuid().ToString("N"));
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
            Name = "roboflags",
            SourcePath = src,
            DestinationPath = dest,
            SourceKind = SourceKind.Folder,
            Options = new JobOptions { RetryCount = 1, RetryWaitSeconds = 1 }
        };

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
