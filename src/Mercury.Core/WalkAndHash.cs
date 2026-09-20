using System.IO.Hashing;

namespace Mercury;

public static class HashUtil
{
    public const int BufferSize = 1024 * 1024;

    public static string ToHex(byte[] hash) => Convert.ToHexString(hash);

    public static string HashFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            BufferSize,
            FileOptions.SequentialScan);
        return HashStream(stream);
    }

    public static string HashStream(Stream stream)
    {
        var hasher = new XxHash64();
        var buffer = new byte[BufferSize];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            hasher.Append(buffer.AsSpan(0, read));
        }

        return ToHex(hasher.GetCurrentHash());
    }
}

public static class SourceWalker
{
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
        MatchType = MatchType.Simple,
        MatchCasing = MatchCasing.CaseInsensitive
    };

    private static readonly HashSet<string> SkipDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System Volume Information",
        "$Recycle.Bin",
        "Recycled",
        "Recycler",
        "Recovery",
        "$WinREAgent"
    };

    public static IEnumerable<FileRecord> Walk(
        CopyMapping mapping,
        IEnumerable<string>? extraExcludeRoots = null,
        JobOptions? options = null)
    {
        var excludes = new List<string>();
        if (extraExcludeRoots is not null)
        {
            excludes.AddRange(extraExcludeRoots);
        }

        if (PathNormalizer.IsUnder(mapping.DestRoot, mapping.SourceRoot))
        {
            excludes.Add(mapping.DestRoot);
        }

        if (mapping.SingleFile)
        {
            var sourceFile = Path.Combine(mapping.SourceRoot, mapping.SingleFileName!);
            var info = new FileInfo(sourceFile);
            if (ShouldSkipFile(info, options))
            {
                yield break;
            }

            yield return new FileRecord
            {
                RelativePath = mapping.SingleFileName!,
                SourcePath = info.FullName,
                DestPath = Path.Combine(mapping.DestRoot, mapping.SingleFileName!),
                Size = info.Length,
                LastWriteUtc = info.LastWriteTimeUtc,
                Status = FileCopyStatus.Pending
            };
            yield break;
        }

        foreach (var record in WalkDirectory(mapping.SourceRoot, mapping.SourceRoot, mapping.DestRoot, excludes, options))
        {
            yield return record;
        }
    }

    public static IEnumerable<DirectoryRecord> WalkDirectories(CopyMapping mapping, JobOptions? options = null)
    {
        if (mapping.SingleFile || !Directory.Exists(mapping.SourceRoot))
        {
            yield break;
        }

        var excludes = new List<string>();
        if (PathNormalizer.IsUnder(mapping.DestRoot, mapping.SourceRoot))
        {
            excludes.Add(mapping.DestRoot);
        }

        foreach (var record in WalkDirectoryRecords(mapping.SourceRoot, mapping.SourceRoot, mapping.DestRoot, excludes, options))
        {
            yield return record;
        }
    }

    private static IEnumerable<FileRecord> WalkDirectory(
        string current,
        string sourceRoot,
        string destRoot,
        List<string> excludes,
        JobOptions? options)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(current, "*", Options);
        }
        catch
        {
            yield break;
        }

        foreach (var file in files)
        {
            FileInfo info;
            try
            {
                info = new FileInfo(file);
                if (ShouldSkipFile(info, options))
                {
                    continue;
                }
            }
            catch
            {
                continue;
            }

            if (excludes.Any(ex => PathsEqual(info.FullName, ex)))
            {
                continue;
            }

            var relative = Path.GetRelativePath(sourceRoot, info.FullName);
            yield return new FileRecord
            {
                RelativePath = relative,
                SourcePath = info.FullName,
                DestPath = PathNormalizer.Combine(destRoot, relative),
                Size = info.Length,
                LastWriteUtc = info.LastWriteTimeUtc,
                Status = FileCopyStatus.Pending
            };
        }

        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(current, "*", Options);
        }
        catch
        {
            yield break;
        }

        foreach (var dir in dirs)
        {
            DirectoryInfo info;
            try
            {
                info = new DirectoryInfo(dir);
            }
            catch
            {
                continue;
            }

            if (ShouldSkipDirectory(info, excludes, options, out var skipChildren))
            {
                continue;
            }

            if (skipChildren)
            {
                continue;
            }

            foreach (var child in WalkDirectory(info.FullName, sourceRoot, destRoot, excludes, options))
            {
                yield return child;
            }
        }
    }

    private static IEnumerable<DirectoryRecord> WalkDirectoryRecords(
        string current,
        string sourceRoot,
        string destRoot,
        List<string> excludes,
        JobOptions? options)
    {
        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(current, "*", Options);
        }
        catch
        {
            yield break;
        }

        foreach (var dir in dirs)
        {
            DirectoryInfo info;
            try
            {
                info = new DirectoryInfo(dir);
            }
            catch
            {
                continue;
            }

            if (ShouldSkipDirectory(info, excludes, options, out var skipChildren))
            {
                continue;
            }

            var relative = Path.GetRelativePath(sourceRoot, info.FullName);
            var isLink = info.Attributes.HasFlag(FileAttributes.ReparsePoint);
            yield return new DirectoryRecord(
                relative,
                info.FullName,
                PathNormalizer.Combine(destRoot, relative),
                isLink,
                isLink ? info.LinkTarget : null);

            if (skipChildren || isLink)
            {
                continue;
            }

            foreach (var child in WalkDirectoryRecords(info.FullName, sourceRoot, destRoot, excludes, options))
            {
                yield return child;
            }
        }
    }

    private static bool ShouldSkipFile(FileInfo info, JobOptions? options)
    {
        if (options?.ExcludeHiddenSystem == true && FileMetadata.IsHiddenOrSystem(info.Attributes))
        {
            return true;
        }

        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) &&
            !CloudPath.IsCloudPlaceholder(info.Attributes) &&
            options?.CopySymbolicLinksAsLinks != true)
        {
            return true;
        }

        return false;
    }

    private static bool ShouldSkipDirectory(
        DirectoryInfo info,
        List<string> excludes,
        JobOptions? options,
        out bool skipChildren)
    {
        skipChildren = false;
        if (SkipDirectoryNames.Contains(info.Name))
        {
            return true;
        }

        if (options?.ExcludeHiddenSystem == true && FileMetadata.IsHiddenOrSystem(info.Attributes))
        {
            return true;
        }

        if (excludes.Any(ex => PathNormalizer.IsUnder(info.FullName, ex) ||
                               string.Equals(info.FullName.TrimEnd('\\'), ex.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            skipChildren = true;
            return options?.CopySymbolicLinksAsLinks != true;
        }

        return false;
    }

    public static TreeCounts CountSource(
        CopyMapping mapping,
        JobOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (mapping.SingleFile)
        {
            var sourceFile = Path.Combine(mapping.SourceRoot, mapping.SingleFileName!);
            if (!File.Exists(sourceFile))
            {
                return default;
            }

            try
            {
                if (ShouldSkipFile(new FileInfo(sourceFile), options))
                {
                    return default;
                }
            }
            catch
            {
                return default;
            }

            return new TreeCounts(1, 0);
        }

        var excludes = new List<string>();
        if (PathNormalizer.IsUnder(mapping.DestRoot, mapping.SourceRoot))
        {
            excludes.Add(mapping.DestRoot);
        }

        return CountTree(mapping.SourceRoot, excludes, skipMercuryTemp: false, options, cancellationToken);
    }

    public static TreeCounts CountDest(CopyMapping mapping, CancellationToken cancellationToken = default)
    {
        if (mapping.SingleFile)
        {
            var destFile = Path.Combine(mapping.DestRoot, mapping.SingleFileName!);
            return File.Exists(destFile) ? new TreeCounts(1, 0) : default;
        }

        return CountTree(mapping.DestRoot, extraExcludeRoots: null, skipMercuryTemp: true, options: null, cancellationToken);
    }

    public static TreeCounts CountTree(
        string root,
        IEnumerable<string>? extraExcludeRoots = null,
        bool skipMercuryTemp = false,
        JobOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (File.Exists(root))
        {
            return new TreeCounts(1, 0);
        }

        if (!Directory.Exists(root))
        {
            return default;
        }

        var excludes = extraExcludeRoots?.ToList() ?? [];
        var files = 0;
        var folders = 0;
        CountDirectory(root, excludes, skipMercuryTemp, options, ref files, ref folders, cancellationToken);
        return new TreeCounts(files, folders);
    }

    private static void CountDirectory(
        string current,
        List<string> excludes,
        bool skipMercuryTemp,
        JobOptions? options,
        ref int files,
        ref int folders,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IEnumerable<string> filePaths;
        try
        {
            filePaths = Directory.EnumerateFiles(current, "*", Options);
        }
        catch
        {
            filePaths = [];
        }

        foreach (var file in filePaths)
        {
            FileInfo info;
            try
            {
                info = new FileInfo(file);
                if (ShouldSkipFile(info, options))
                {
                    continue;
                }
            }
            catch
            {
                continue;
            }

            if (skipMercuryTemp && info.Name.EndsWith(".mercury.tmp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            files++;
        }

        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(current, "*", Options);
        }
        catch
        {
            return;
        }

        foreach (var dir in dirs)
        {
            DirectoryInfo info;
            try
            {
                info = new DirectoryInfo(dir);
            }
            catch
            {
                continue;
            }

            if (ShouldSkipDirectory(info, excludes, options, out var skipChildren))
            {
                continue;
            }

            folders++;
            if (skipChildren)
            {
                continue;
            }

            CountDirectory(info.FullName, excludes, skipMercuryTemp, options, ref files, ref folders, cancellationToken);
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd('\\'),
                Path.GetFullPath(right).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}

public readonly record struct TreeCounts(int Files, int Folders);
