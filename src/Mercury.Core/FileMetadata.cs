using System.Security.AccessControl;

namespace Mercury;

/// <summary>
/// Native metadata apply after copy (not robocopy.exe). Fail-soft for ACL/owner.
/// </summary>
public static class FileMetadata
{
    private const FileAttributes CopiedFileAttributes =
        FileAttributes.ReadOnly |
        FileAttributes.Hidden |
        FileAttributes.System |
        FileAttributes.Archive |
        FileAttributes.NotContentIndexed |
        FileAttributes.Temporary |
        FileAttributes.Offline;

    public static TimeSpan ComparisonTolerance(JobOptions options) =>
        options.FatTimestampTolerance ? CopyEngine.FatTimestampTolerance : TimeSpan.Zero;

    public static FileOptions SequentialIo(JobOptions? options)
    {
        var flags = FileOptions.SequentialScan | FileOptions.Asynchronous;
        if (options?.UnbufferedIo == true)
        {
            flags |= FileOptions.WriteThrough;
        }

        return flags;
    }

    public static bool IsHiddenOrSystem(FileAttributes attributes) =>
        attributes.HasFlag(FileAttributes.Hidden) || attributes.HasFlag(FileAttributes.System);

    public static bool IsSymlinkFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var attrs = File.GetAttributes(path);
            return attrs.HasFlag(FileAttributes.ReparsePoint) && !CloudPath.IsCloudPlaceholder(attrs);
        }
        catch
        {
            return false;
        }
    }

    public static string DescribeFlags(JobOptions options)
    {
        var bits = new List<string>
        {
            options.CopyTimestamps ? "timestamps on (/COPY:T)" : "timestamps off",
            options.CopyAttributes ? "attributes on (/COPY:A)" : "attributes off",
            options.CopySecurity ? "ACL on (/COPY:S)" : "ACL off",
            options.CopyOwner ? "owner on (/COPY:O)" : "owner off",
            options.CopyDirectoryTimestamps ? "dir timestamps on (/DCOPY:T)" : "dir timestamps off",
            options.CopyEmptyDirectories ? "empty dirs on (/E)" : "empty dirs off (/S)",
            options.UnbufferedIo ? "unbuffered I/O on (/J)" : "unbuffered I/O off",
            options.CopySymbolicLinksAsLinks ? "copy symlinks as links (/SL)" : "skip symlink reparse (as now)",
            options.FatTimestampTolerance ? "FAT 2s on (/FFT)" : "FAT 2s off",
            options.ExcludeHiddenSystem ? "exclude hidden/system on (/XA:HS)" : "exclude hidden/system off",
            options.PurgeExtraDestFiles ? "purge extra dest on (/PURGE)" : "purge off"
        };
        return "RoboFlags: " + string.Join("; ", bits) + ".";
    }

    public static void ApplyCopiedFile(
        string sourcePath,
        string destPath,
        JobOptions options,
        IJobLog? log,
        string jobId,
        string jobName)
    {
        if (!File.Exists(destPath))
        {
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                ApplySecurity(sourcePath, destPath, options, log, jobId, jobName, isDirectory: false);
            }

            ApplyAttributesThenTimestamps(sourcePath, destPath, options, isDirectory: false);
        }
        catch (Exception ex)
        {
            log?.Info(jobId, jobName, $"Metadata not fully applied for {destPath}: {ex.Message}");
        }
    }

    public static void ApplyCopiedDirectory(
        string sourcePath,
        string destPath,
        JobOptions options,
        IJobLog? log,
        string jobId,
        string jobName)
    {
        if (!Directory.Exists(destPath) || !Directory.Exists(sourcePath))
        {
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                ApplySecurity(sourcePath, destPath, options, log, jobId, jobName, isDirectory: true);
            }

            if (options.CopyAttributes || options.CopyDirectoryTimestamps)
            {
                ApplyAttributesThenTimestamps(sourcePath, destPath, options, isDirectory: true);
            }
        }
        catch (Exception ex)
        {
            log?.Info(jobId, jobName, $"Directory metadata not fully applied for {destPath}: {ex.Message}");
        }
    }

    public static bool TryCopySymlinkFile(
        string sourcePath,
        string destPath,
        IJobLog? log,
        string jobId,
        string jobName)
    {
        if (!IsSymlinkFile(sourcePath))
        {
            return false;
        }

        var target = new FileInfo(sourcePath).LinkTarget;
        if (string.IsNullOrEmpty(target))
        {
            throw new IOException($"Symbolic link has no target: {sourcePath}");
        }

        var destDir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(destDir))
        {
            Directory.CreateDirectory(destDir);
        }

        TryReplaceDest(destPath);
        try
        {
            File.CreateSymbolicLink(destPath, target);
        }
        catch (Exception ex)
        {
            log?.Info(jobId, jobName,
                $"Could not create symbolic link {destPath} → {target}: {ex.Message}. Developer Mode or elevation may be required for /SL.");
            throw;
        }

        return true;
    }

    public static void FinishDestination(
        Job job,
        CopyMapping mapping,
        JobJournal journal,
        IJobLog log,
        string name)
    {
        if (mapping.SingleFile || job.Catcher is not null)
        {
            return;
        }

        var dirs = SourceWalker.WalkDirectories(mapping, job.Options).ToList();
        if (job.Options.CopyEmptyDirectories || job.Options.CopySymbolicLinksAsLinks)
        {
            foreach (var dir in dirs)
            {
                try
                {
                    if (dir.IsSymbolicLink)
                    {
                        if (job.Options.CopySymbolicLinksAsLinks)
                        {
                            CreateDirectoryLink(dir, log, job.Id, name);
                        }

                        continue;
                    }

                    if (job.Options.CopyEmptyDirectories)
                    {
                        Directory.CreateDirectory(dir.DestPath);
                    }
                }
                catch (Exception ex)
                {
                    log.Info(job.Id, name, $"Directory {dir.RelativePath}: {ex.Message}");
                }
            }
        }

        if (job.Options.PurgeExtraDestFiles)
        {
            PurgeExtra(mapping, journal.GetFiles(), job.Options.CopyEmptyDirectories ? dirs : [], log, job.Id, name);
        }

        if (job.Options.CopyDirectoryTimestamps ||
            job.Options.CopyAttributes || job.Options.CopySecurity || job.Options.CopyOwner)
        {
            foreach (var dir in dirs.OrderByDescending(d => d.RelativePath.Length))
            {
                if (dir.IsSymbolicLink || !Directory.Exists(dir.DestPath))
                {
                    continue;
                }

                ApplyCopiedDirectory(dir.SourcePath, dir.DestPath, job.Options, log, job.Id, name);
            }

            ApplyCopiedDirectory(mapping.SourceRoot, mapping.DestRoot, job.Options, log, job.Id, name);
        }
    }

    private static void CreateDirectoryLink(DirectoryRecord dir, IJobLog log, string jobId, string jobName)
    {
        var target = dir.LinkTarget ?? new DirectoryInfo(dir.SourcePath).LinkTarget;
        if (string.IsNullOrEmpty(target))
        {
            log.Info(jobId, jobName, $"Directory symlink has no target: {dir.SourcePath}");
            return;
        }

        var parent = Path.GetDirectoryName(dir.DestPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        if (Directory.Exists(dir.DestPath) || File.Exists(dir.DestPath))
        {
            return;
        }

        try
        {
            Directory.CreateSymbolicLink(dir.DestPath, target);
        }
        catch (Exception ex)
        {
            log.Info(jobId, jobName,
                $"Could not create directory symlink {dir.DestPath} → {target}: {ex.Message}. Developer Mode or elevation may be required for /SL.");
        }
    }

    private static void PurgeExtra(
        CopyMapping mapping,
        IReadOnlyList<FileRecord> files,
        IReadOnlyList<DirectoryRecord> sourceDirs,
        IJobLog log,
        string jobId,
        string jobName)
    {
        if (!Directory.Exists(mapping.DestRoot))
        {
            return;
        }

        var destRoot = Path.GetFullPath(mapping.DestRoot);
        var keepFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keepDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { destRoot };
        foreach (var file in files)
        {
            try
            {
                keepFiles.Add(Path.GetFullPath(file.DestPath));
                RememberAncestors(keepDirs, file.DestPath, destRoot);
            }
            catch
            {
                // skip bad paths
            }
        }

        foreach (var dir in sourceDirs)
        {
            try
            {
                keepDirs.Add(Path.GetFullPath(dir.DestPath));
                RememberAncestors(keepDirs, dir.DestPath, destRoot);
            }
            catch
            {
                // skip
            }
        }

        var removedFiles = 0;
        var removedDirs = 0;
        foreach (var destFile in EnumerateDestFiles(destRoot))
        {
            if (keepFiles.Contains(destFile))
            {
                continue;
            }

            try
            {
                File.SetAttributes(destFile, FileAttributes.Normal);
                File.Delete(destFile);
                removedFiles++;
            }
            catch (Exception ex)
            {
                log.Info(jobId, jobName, $"Purge could not delete file {destFile}: {ex.Message}");
            }
        }

        foreach (var destDir in EnumerateDestDirsDeepestFirst(destRoot))
        {
            if (keepDirs.Contains(destDir))
            {
                continue;
            }

            try
            {
                Directory.Delete(destDir, recursive: true);
                removedDirs++;
            }
            catch (Exception ex)
            {
                log.Info(jobId, jobName, $"Purge could not delete folder {destDir}: {ex.Message}");
            }
        }

        if (removedFiles > 0 || removedDirs > 0)
        {
            log.Info(jobId, jobName, $"Purge (/PURGE): removed {removedFiles} extra file(s) and {removedDirs} extra folder(s).");
        }
    }

    private static void RememberAncestors(HashSet<string> keepDirs, string path, string destRoot)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        while (!string.IsNullOrEmpty(dir))
        {
            var full = Path.GetFullPath(dir);
            keepDirs.Add(full);
            if (string.Equals(full, destRoot, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            dir = Path.GetDirectoryName(full);
        }
    }

    private static IEnumerable<string> EnumerateDestFiles(string destRoot)
    {
        var stack = new Stack<string>();
        stack.Push(destRoot);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(current);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return Path.GetFullPath(file);
            }

            IEnumerable<string> dirs;
            try
            {
                dirs = Directory.EnumerateDirectories(current);
            }
            catch
            {
                continue;
            }

            foreach (var dir in dirs)
            {
                try
                {
                    var info = new DirectoryInfo(dir);
                    if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }

                stack.Push(dir);
            }
        }
    }

    private static IEnumerable<string> EnumerateDestDirsDeepestFirst(string destRoot)
    {
        var all = new List<string>();
        CollectDirs(destRoot, all);
        return all
            .Where(d => !string.Equals(d, destRoot, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.Length);
    }

    private static void CollectDirs(string current, List<string> all)
    {
        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(current);
        }
        catch
        {
            return;
        }

        foreach (var dir in dirs)
        {
            try
            {
                var info = new DirectoryInfo(dir);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                var full = info.FullName;
                all.Add(full);
                CollectDirs(full, all);
            }
            catch
            {
                // skip
            }
        }
    }

    private static void ApplyAttributesThenTimestamps(string sourcePath, string destPath, JobOptions options, bool isDirectory)
    {
        FileAttributes? copied = null;
        if (options.CopyAttributes)
        {
            var srcAttrs = File.GetAttributes(sourcePath);
            copied = srcAttrs & CopiedFileAttributes;
            if (isDirectory)
            {
                copied |= FileAttributes.Directory;
            }
        }

        var destAttrs = File.GetAttributes(destPath);
        if (destAttrs.HasFlag(FileAttributes.ReadOnly))
        {
            File.SetAttributes(destPath, destAttrs & ~FileAttributes.ReadOnly);
        }

        if (copied is { } attrsWithoutRo)
        {
            File.SetAttributes(destPath, attrsWithoutRo & ~FileAttributes.ReadOnly);
        }

        var copyTimes = isDirectory ? options.CopyDirectoryTimestamps : options.CopyTimestamps;
        if (copyTimes)
        {
            DateTime created;
            DateTime written;
            if (isDirectory)
            {
                created = Directory.GetCreationTimeUtc(sourcePath);
                written = Directory.GetLastWriteTimeUtc(sourcePath);
                Directory.SetCreationTimeUtc(destPath, created);
                Directory.SetLastWriteTimeUtc(destPath, written);
            }
            else
            {
                created = File.GetCreationTimeUtc(sourcePath);
                written = File.GetLastWriteTimeUtc(sourcePath);
                File.SetCreationTimeUtc(destPath, created);
                File.SetLastWriteTimeUtc(destPath, written);
            }
        }

        if (copied is { } finalAttrs)
        {
            File.SetAttributes(destPath, finalAttrs);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void ApplySecurity(
        string sourcePath,
        string destPath,
        JobOptions options,
        IJobLog? log,
        string jobId,
        string jobName,
        bool isDirectory)
    {
        if (!options.CopySecurity && !options.CopyOwner)
        {
            return;
        }

        if (options.CopySecurity)
        {
            try
            {
                if (isDirectory)
                {
                    var acl = new DirectoryInfo(sourcePath).GetAccessControl(AccessControlSections.Access);
                    new DirectoryInfo(destPath).SetAccessControl(acl);
                }
                else
                {
                    var acl = new FileInfo(sourcePath).GetAccessControl(AccessControlSections.Access);
                    new FileInfo(destPath).SetAccessControl(acl);
                }
            }
            catch (Exception ex)
            {
                log?.Info(jobId, jobName, $"ACL (/COPY:S) not copied for {destPath}: {ex.Message}");
            }
        }

        if (options.CopyOwner)
        {
            try
            {
                if (isDirectory)
                {
                    var owner = new DirectoryInfo(sourcePath).GetAccessControl(AccessControlSections.Owner);
                    new DirectoryInfo(destPath).SetAccessControl(owner);
                }
                else
                {
                    var owner = new FileInfo(sourcePath).GetAccessControl(AccessControlSections.Owner);
                    new FileInfo(destPath).SetAccessControl(owner);
                }
            }
            catch (Exception ex)
            {
                log?.Info(jobId, jobName,
                    $"Owner (/COPY:O) not copied for {destPath}: {ex.Message}. Run Mercury elevated if you need owner.");
            }
        }
    }

    private static void TryReplaceDest(string destPath)
    {
        try
        {
            if (File.Exists(destPath))
            {
                File.Delete(destPath);
            }
        }
        catch
        {
            // leave for CreateSymbolicLink to fail
        }
    }
}
