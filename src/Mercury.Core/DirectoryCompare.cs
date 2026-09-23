using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Mercury;

[Flags]
public enum CompareFilter
{
    None = 0,
    FolderCounts = 1 << 0,
    FileCounts = 1 << 1,
    Size = 1 << 2,
    Timestamp = 1 << 3,
    Hash = 1 << 4,
    Name = 1 << 5,
    PerFolderFileCounts = 1 << 6
}

public enum CompareDiffKind
{
    OnlyLeftFile,
    OnlyRightFile,
    OnlyLeftFolder,
    OnlyRightFolder,
    SizeMismatch,
    TimestampMismatch,
    HashMismatch,
    NameMismatch,
    FolderFileCountMismatch
}

public sealed class DirectoryCompareOptions
{
    public static DirectoryCompareOptions Default { get; } = new();

    public bool Advanced { get; init; }
    public bool Hash { get; init; }
    public bool FatTimestampTolerance { get; init; }
    public CompareFilter Filter { get; init; } = CompareFilter.FolderCounts | CompareFilter.FileCounts;
    public int MaxListedDifferences { get; init; } = 2_000;
    public int HighlightLimit { get; init; } = 5;
}

public sealed class DirectoryCompareProgress
{
    public DirectoryCompareProgress(int filesVisited, int foldersVisited, string? currentRelative)
    {
        FilesVisited = filesVisited;
        FoldersVisited = foldersVisited;
        CurrentRelative = currentRelative;
    }

    public int FilesVisited { get; }
    public int FoldersVisited { get; }
    public string? CurrentRelative { get; }
}

public sealed class DirectoryCompareDiff
{
    public CompareDiffKind Kind { get; init; }
    public bool IsFolder { get; init; }
    public string RelativePath { get; init; } = "";
    public string Detail { get; init; } = "";
    public long? LeftSize { get; init; }
    public long? RightSize { get; init; }
    public DateTime? LeftWriteUtc { get; init; }
    public DateTime? RightWriteUtc { get; init; }
    public int? LeftFileCount { get; init; }
    public int? RightFileCount { get; init; }

    public string KindLabel => Kind switch
    {
        CompareDiffKind.OnlyLeftFile => "only left · file",
        CompareDiffKind.OnlyRightFile => "only right · file",
        CompareDiffKind.OnlyLeftFolder => "only left · folder",
        CompareDiffKind.OnlyRightFolder => "only right · folder",
        CompareDiffKind.SizeMismatch => "size",
        CompareDiffKind.TimestampMismatch => "timestamp",
        CompareDiffKind.HashMismatch => "hash",
        CompareDiffKind.NameMismatch => "name",
        CompareDiffKind.FolderFileCountMismatch => "files in folder",
        _ => Kind.ToString()
    };

    public string ListText =>
        string.IsNullOrWhiteSpace(Detail)
            ? $"{KindLabel}  {DisplayPath}"
            : $"{KindLabel}  {DisplayPath}  —  {Detail}";

    public string DisplayPath => string.IsNullOrEmpty(RelativePath) ? "." : RelativePath;

    public static bool IsIncluded(CompareDiffKind kind, CompareFilter filter) => kind switch
    {
        CompareDiffKind.OnlyLeftFile or CompareDiffKind.OnlyRightFile => filter.HasFlag(CompareFilter.FileCounts),
        CompareDiffKind.OnlyLeftFolder or CompareDiffKind.OnlyRightFolder => filter.HasFlag(CompareFilter.FolderCounts),
        CompareDiffKind.SizeMismatch => filter.HasFlag(CompareFilter.Size),
        CompareDiffKind.TimestampMismatch => filter.HasFlag(CompareFilter.Timestamp),
        CompareDiffKind.HashMismatch => filter.HasFlag(CompareFilter.Hash),
        CompareDiffKind.NameMismatch => filter.HasFlag(CompareFilter.Name),
        CompareDiffKind.FolderFileCountMismatch => filter.HasFlag(CompareFilter.PerFolderFileCounts),
        _ => false
    };
}

public sealed class DirectoryCompareHighlight
{
    public string Title { get; init; } = "";
    public IReadOnlyList<string> Lines { get; init; } = [];
}

public sealed class DirectoryCompareResult
{
    public string LeftRoot { get; init; } = "";
    public string RightRoot { get; init; } = "";
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset EndedUtc { get; init; }
    public bool Advanced { get; init; }
    public bool Hashed { get; init; }
    public bool FatTimestampTolerance { get; init; }
    public bool Canceled { get; init; }
    public bool Completed { get; init; }
    public string? Error { get; init; }
    public int FilesVisited { get; init; }
    public int FoldersVisited { get; init; }

    public int LeftFolders { get; init; }
    public int RightFolders { get; init; }
    public int FoldersOnlyLeft { get; init; }
    public int FoldersOnlyRight { get; init; }
    public int FoldersMatchingName { get; init; }
    public int LeftFiles { get; init; }
    public int RightFiles { get; init; }
    public int FilesOnlyLeft { get; init; }
    public int FilesOnlyRight { get; init; }
    public int FilesSameRelativePath { get; init; }
    public int SizeMismatches { get; init; }
    public int TimestampMismatches { get; init; }
    public int HashMismatches { get; init; }
    public int NameMismatches { get; init; }
    public int FolderFileCountMismatches { get; init; }

    /// <summary>Every computed difference (scan mode), unfiltered and uncapped — for export and re-filter.</summary>
    public IReadOnlyList<DirectoryCompareDiff> Differences { get; init; } = [];

    public IEnumerable<DirectoryCompareDiff> Filtered(CompareFilter filter) =>
        Differences.Where(d => DirectoryCompareDiff.IsIncluded(d.Kind, filter));

    public IReadOnlyList<DirectoryCompareDiff> Listed(CompareFilter filter, int cap)
    {
        if (cap <= 0)
        {
            return Filtered(filter).ToList();
        }

        return Filtered(filter).Take(cap).ToList();
    }

    public IReadOnlyList<DirectoryCompareHighlight> Highlights(CompareFilter filter, int limit)
    {
        limit = Math.Max(1, limit);
        var cards = new List<DirectoryCompareHighlight>();
        var diffs = Differences;

        void AddCard(string title, IEnumerable<string> lines)
        {
            var list = lines.Take(limit).ToList();
            if (list.Count > 0)
            {
                cards.Add(new DirectoryCompareHighlight { Title = title, Lines = list });
            }
        }

        if (filter.HasFlag(CompareFilter.FolderCounts))
        {
            AddCard(
                "Only-left folders",
                diffs.Where(d => d.Kind == CompareDiffKind.OnlyLeftFolder)
                    .OrderByDescending(d => d.LeftFileCount ?? 0)
                    .ThenBy(d => d.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .Select(d => FolderLine(d, left: true)));
            AddCard(
                "Only-right folders",
                diffs.Where(d => d.Kind == CompareDiffKind.OnlyRightFolder)
                    .OrderByDescending(d => d.RightFileCount ?? 0)
                    .ThenBy(d => d.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .Select(d => FolderLine(d, left: false)));
        }

        if (filter.HasFlag(CompareFilter.FileCounts))
        {
            AddCard(
                "Only-left files",
                diffs.Where(d => d.Kind == CompareDiffKind.OnlyLeftFile)
                    .OrderByDescending(d => d.LeftSize ?? 0)
                    .ThenBy(d => d.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .Select(FileLine));
            AddCard(
                "Only-right files",
                diffs.Where(d => d.Kind == CompareDiffKind.OnlyRightFile)
                    .OrderByDescending(d => d.RightSize ?? 0)
                    .ThenBy(d => d.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .Select(FileLine));
        }

        if (filter.HasFlag(CompareFilter.Size))
        {
            AddCard(
                "Largest size mismatches",
                diffs.Where(d => d.Kind == CompareDiffKind.SizeMismatch)
                    .OrderByDescending(d => Math.Abs((d.LeftSize ?? 0) - (d.RightSize ?? 0)))
                    .Select(d => $"{d.DisplayPath}  {ByteFormatter.ToString(d.LeftSize ?? 0)} vs {ByteFormatter.ToString(d.RightSize ?? 0)}"));
        }

        if (filter.HasFlag(CompareFilter.Timestamp))
        {
            AddCard(
                "Newest time mismatches",
                diffs.Where(d => d.Kind == CompareDiffKind.TimestampMismatch)
                    .OrderByDescending(d => MaxWrite(d))
                    .Select(d => $"{d.DisplayPath}  {FormatStamp(d.LeftWriteUtc)} vs {FormatStamp(d.RightWriteUtc)}"));
        }

        if (filter.HasFlag(CompareFilter.Hash))
        {
            AddCard(
                "Hash failures",
                diffs.Where(d => d.Kind == CompareDiffKind.HashMismatch)
                    .OrderBy(d => d.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .Select(d => string.IsNullOrEmpty(d.Detail) ? d.DisplayPath : $"{d.DisplayPath}  {d.Detail}"));
        }

        if (filter.HasFlag(CompareFilter.Name))
        {
            AddCard(
                "Name issues",
                diffs.Where(d => d.Kind == CompareDiffKind.NameMismatch)
                    .OrderBy(d => d.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .Select(d => string.IsNullOrEmpty(d.Detail) ? d.DisplayPath : $"{d.DisplayPath}  {d.Detail}"));
        }

        if (filter.HasFlag(CompareFilter.PerFolderFileCounts))
        {
            AddCard(
                "Folder file-count mismatches",
                diffs.Where(d => d.Kind == CompareDiffKind.FolderFileCountMismatch)
                    .OrderByDescending(d => Math.Abs((d.LeftFileCount ?? 0) - (d.RightFileCount ?? 0)))
                    .Select(d => $"{d.DisplayPath}  {d.LeftFileCount ?? 0} vs {d.RightFileCount ?? 0} files"));
        }

        return cards;
    }

    private static string FolderLine(DirectoryCompareDiff diff, bool left)
    {
        var n = left ? diff.LeftFileCount ?? 0 : diff.RightFileCount ?? 0;
        return n == 0 ? diff.DisplayPath : $"{diff.DisplayPath}  ({n} files in tree)";
    }

    private static string FileLine(DirectoryCompareDiff diff)
    {
        var size = diff.LeftSize ?? diff.RightSize ?? 0;
        return size <= 0 ? diff.DisplayPath : $"{diff.DisplayPath}  {ByteFormatter.ToString(size)}";
    }

    private static DateTime MaxWrite(DirectoryCompareDiff diff)
    {
        var left = diff.LeftWriteUtc ?? DateTime.MinValue;
        var right = diff.RightWriteUtc ?? DateTime.MinValue;
        return left >= right ? left : right;
    }

    private static string FormatStamp(DateTime? utc) =>
        utc is { } value
            ? TransferRundown.FormatLogTime(value)
            : "—";
}

public static class DirectoryComparer
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

    public static DirectoryCompareResult Compare(
        string leftRoot,
        string rightRoot,
        DirectoryCompareOptions? options = null,
        CancellationToken cancellationToken = default,
        PauseGate? pause = null,
        Action<DirectoryCompareProgress>? progress = null)
    {
        options ??= DirectoryCompareOptions.Default;
        var started = DateTimeOffset.UtcNow;
        try
        {
            var leftPath = PathNormalizer.Normalize(leftRoot);
            var rightPath = PathNormalizer.Normalize(rightRoot);
            if (!Directory.Exists(leftPath))
            {
                return Failed(leftPath, rightRoot, started, $"Left folder not found: {leftPath}");
            }

            if (!Directory.Exists(rightPath))
            {
                return Failed(leftPath, rightPath, started, $"Right folder not found: {rightPath}");
            }

            var filesVisited = 0;
            var foldersVisited = 0;
            var lastPulse = Stopwatch.GetTimestamp();
            var left = ScanSide(leftPath, cancellationToken, pause, progress, ref filesVisited, ref foldersVisited, ref lastPulse);
            var right = ScanSide(rightPath, cancellationToken, pause, progress, ref filesVisited, ref foldersVisited, ref lastPulse);
            Pulse(progress, filesVisited, foldersVisited, null, ref lastPulse, force: true);

            var diffs = new List<DirectoryCompareDiff>();
            var advanced = options.Advanced;
            var hash = options.Hash && advanced;
            var tolerance = options.FatTimestampTolerance
                ? CopyEngine.FatTimestampTolerance
                : TimeSpan.Zero;

            CompareFolders(left, right, advanced, diffs);
            CompareFiles(left, right, advanced, hash, tolerance, diffs, cancellationToken, pause, progress, ref filesVisited, ref foldersVisited, ref lastPulse);

            var result = BuildResult(leftPath, rightPath, started, DateTimeOffset.UtcNow, options, left, right, diffs, filesVisited, foldersVisited, canceled: false, error: null);
            Pulse(progress, filesVisited, foldersVisited, null, ref lastPulse, force: true);
            return result;
        }
        catch (OperationCanceledException)
        {
            return new DirectoryCompareResult
            {
                LeftRoot = leftRoot,
                RightRoot = rightRoot,
                StartedUtc = started,
                EndedUtc = DateTimeOffset.UtcNow,
                Advanced = options.Advanced,
                Hashed = options.Hash && options.Advanced,
                FatTimestampTolerance = options.FatTimestampTolerance,
                Canceled = true,
                Completed = false
            };
        }
        catch (Exception ex)
        {
            return Failed(leftRoot, rightRoot, started, ex.Message, options);
        }
    }

    private static DirectoryCompareResult Failed(
        string left,
        string right,
        DateTimeOffset started,
        string error,
        DirectoryCompareOptions? options = null) =>
        new()
        {
            LeftRoot = left,
            RightRoot = right,
            StartedUtc = started,
            EndedUtc = DateTimeOffset.UtcNow,
            Advanced = options?.Advanced == true,
            Hashed = options is { Hash: true, Advanced: true },
            FatTimestampTolerance = options?.FatTimestampTolerance == true,
            Completed = false,
            Error = error
        };

    private static DirectoryCompareResult BuildResult(
        string leftPath,
        string rightPath,
        DateTimeOffset started,
        DateTimeOffset ended,
        DirectoryCompareOptions options,
        TreeSide left,
        TreeSide right,
        List<DirectoryCompareDiff> diffs,
        int filesVisited,
        int foldersVisited,
        bool canceled,
        string? error)
    {
        var onlyLeftFolders = diffs.Count(d => d.Kind == CompareDiffKind.OnlyLeftFolder);
        var onlyRightFolders = diffs.Count(d => d.Kind == CompareDiffKind.OnlyRightFolder);
        var onlyLeftFiles = diffs.Count(d => d.Kind == CompareDiffKind.OnlyLeftFile);
        var onlyRightFiles = diffs.Count(d => d.Kind == CompareDiffKind.OnlyRightFile);
        var matchingFolders = left.Folders.Keys.Count(k => right.Folders.ContainsKey(k));
        var sameFiles = left.Files.Keys.Count(k => right.Files.ContainsKey(k));

        return new DirectoryCompareResult
        {
            LeftRoot = leftPath,
            RightRoot = rightPath,
            StartedUtc = started,
            EndedUtc = ended,
            Advanced = options.Advanced,
            Hashed = options.Hash && options.Advanced,
            FatTimestampTolerance = options.FatTimestampTolerance,
            Canceled = canceled,
            Completed = !canceled && error is null,
            Error = error,
            FilesVisited = filesVisited,
            FoldersVisited = foldersVisited,
            LeftFolders = left.Folders.Count,
            RightFolders = right.Folders.Count,
            FoldersOnlyLeft = onlyLeftFolders,
            FoldersOnlyRight = onlyRightFolders,
            FoldersMatchingName = matchingFolders,
            LeftFiles = left.Files.Count,
            RightFiles = right.Files.Count,
            FilesOnlyLeft = onlyLeftFiles,
            FilesOnlyRight = onlyRightFiles,
            FilesSameRelativePath = sameFiles,
            SizeMismatches = diffs.Count(d => d.Kind == CompareDiffKind.SizeMismatch),
            TimestampMismatches = diffs.Count(d => d.Kind == CompareDiffKind.TimestampMismatch),
            HashMismatches = diffs.Count(d => d.Kind == CompareDiffKind.HashMismatch),
            NameMismatches = diffs.Count(d => d.Kind == CompareDiffKind.NameMismatch),
            FolderFileCountMismatches = diffs.Count(d => d.Kind == CompareDiffKind.FolderFileCountMismatch),
            Differences = diffs
        };
    }

    private static void CompareFolders(TreeSide left, TreeSide right, bool advanced, List<DirectoryCompareDiff> diffs)
    {
        foreach (var (key, folder) in left.Folders)
        {
            if (!right.Folders.TryGetValue(key, out var other))
            {
                diffs.Add(new DirectoryCompareDiff
                {
                    Kind = CompareDiffKind.OnlyLeftFolder,
                    IsFolder = true,
                    RelativePath = key,
                    LeftFileCount = folder.SubtreeFiles
                });
                continue;
            }

            if (!advanced)
            {
                continue;
            }

            if (!string.Equals(folder.Name, other.Name, StringComparison.Ordinal))
            {
                diffs.Add(new DirectoryCompareDiff
                {
                    Kind = CompareDiffKind.NameMismatch,
                    IsFolder = true,
                    RelativePath = key,
                    Detail = $"{folder.Name} vs {other.Name}"
                });
            }

            if (folder.ImmediateFiles != other.ImmediateFiles)
            {
                diffs.Add(new DirectoryCompareDiff
                {
                    Kind = CompareDiffKind.FolderFileCountMismatch,
                    IsFolder = true,
                    RelativePath = key,
                    LeftFileCount = folder.ImmediateFiles,
                    RightFileCount = other.ImmediateFiles,
                    Detail = $"{folder.ImmediateFiles} vs {other.ImmediateFiles} files"
                });
            }
        }

        foreach (var (key, folder) in right.Folders)
        {
            if (left.Folders.ContainsKey(key))
            {
                continue;
            }

            diffs.Add(new DirectoryCompareDiff
            {
                Kind = CompareDiffKind.OnlyRightFolder,
                IsFolder = true,
                RelativePath = key,
                RightFileCount = folder.SubtreeFiles
            });
        }

        if (advanced && left.RootImmediateFiles != right.RootImmediateFiles)
        {
            diffs.Add(new DirectoryCompareDiff
            {
                Kind = CompareDiffKind.FolderFileCountMismatch,
                IsFolder = true,
                RelativePath = "",
                LeftFileCount = left.RootImmediateFiles,
                RightFileCount = right.RootImmediateFiles,
                Detail = $"{left.RootImmediateFiles} vs {right.RootImmediateFiles} files"
            });
        }
    }

    private static void CompareFiles(
        TreeSide left,
        TreeSide right,
        bool advanced,
        bool hash,
        TimeSpan tolerance,
        List<DirectoryCompareDiff> diffs,
        CancellationToken cancellationToken,
        PauseGate? pause,
        Action<DirectoryCompareProgress>? progress,
        ref int filesVisited,
        ref int foldersVisited,
        ref long lastPulse)
    {
        foreach (var (key, file) in left.Files)
        {
            WaitIfPaused(pause, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (!right.Files.TryGetValue(key, out var other))
            {
                diffs.Add(new DirectoryCompareDiff
                {
                    Kind = CompareDiffKind.OnlyLeftFile,
                    RelativePath = key,
                    LeftSize = file.Size,
                    LeftWriteUtc = file.LastWriteUtc
                });
                continue;
            }

            if (!advanced)
            {
                continue;
            }

            if (!string.Equals(file.Name, other.Name, StringComparison.Ordinal))
            {
                diffs.Add(new DirectoryCompareDiff
                {
                    Kind = CompareDiffKind.NameMismatch,
                    RelativePath = key,
                    Detail = $"{file.Name} vs {other.Name}",
                    LeftSize = file.Size,
                    RightSize = other.Size
                });
            }

            if (file.Size != other.Size)
            {
                diffs.Add(new DirectoryCompareDiff
                {
                    Kind = CompareDiffKind.SizeMismatch,
                    RelativePath = key,
                    LeftSize = file.Size,
                    RightSize = other.Size,
                    LeftWriteUtc = file.LastWriteUtc,
                    RightWriteUtc = other.LastWriteUtc,
                    Detail = $"{ByteFormatter.ToString(file.Size)} vs {ByteFormatter.ToString(other.Size)}"
                });
            }

            var delta = file.LastWriteUtc - other.LastWriteUtc;
            if (delta < TimeSpan.Zero)
            {
                delta = -delta;
            }

            if (delta > tolerance)
            {
                diffs.Add(new DirectoryCompareDiff
                {
                    Kind = CompareDiffKind.TimestampMismatch,
                    RelativePath = key,
                    LeftSize = file.Size,
                    RightSize = other.Size,
                    LeftWriteUtc = file.LastWriteUtc,
                    RightWriteUtc = other.LastWriteUtc,
                    Detail = $"{TransferRundown.FormatLogTime(file.LastWriteUtc)} vs {TransferRundown.FormatLogTime(other.LastWriteUtc)}"
                });
            }

            if (hash && file.Size == other.Size)
            {
                WaitIfPaused(pause, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var leftHash = HashUtil.HashFile(file.FullPath);
                    var rightHash = HashUtil.HashFile(other.FullPath);
                    if (!string.Equals(leftHash, rightHash, StringComparison.OrdinalIgnoreCase))
                    {
                        diffs.Add(new DirectoryCompareDiff
                        {
                            Kind = CompareDiffKind.HashMismatch,
                            RelativePath = key,
                            LeftSize = file.Size,
                            RightSize = other.Size,
                            Detail = $"{leftHash} vs {rightHash}"
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    diffs.Add(new DirectoryCompareDiff
                    {
                        Kind = CompareDiffKind.HashMismatch,
                        RelativePath = key,
                        Detail = ex.Message
                    });
                }

                Pulse(progress, filesVisited, foldersVisited, key, ref lastPulse);
            }
        }

        foreach (var (key, file) in right.Files)
        {
            if (left.Files.ContainsKey(key))
            {
                continue;
            }

            diffs.Add(new DirectoryCompareDiff
            {
                Kind = CompareDiffKind.OnlyRightFile,
                RelativePath = key,
                RightSize = file.Size,
                RightWriteUtc = file.LastWriteUtc
            });
        }
    }

    private static TreeSide ScanSide(
        string root,
        CancellationToken cancellationToken,
        PauseGate? pause,
        Action<DirectoryCompareProgress>? progress,
        ref int filesVisited,
        ref int foldersVisited,
        ref long lastPulse)
    {
        var side = new TreeSide();
        Walk(root, "", side, cancellationToken, pause, progress, ref filesVisited, ref foldersVisited, ref lastPulse);
        return side;
    }

    private static void Walk(
        string current,
        string relative,
        TreeSide side,
        CancellationToken cancellationToken,
        PauseGate? pause,
        Action<DirectoryCompareProgress>? progress,
        ref int filesVisited,
        ref int foldersVisited,
        ref long lastPulse)
    {
        WaitIfPaused(pause, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(current, "*", Options);
        }
        catch
        {
            files = [];
        }

        foreach (var file in files)
        {
            WaitIfPaused(pause, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo info;
            try
            {
                info = new FileInfo(file);
                if (ShouldSkipFile(info))
                {
                    continue;
                }
            }
            catch
            {
                continue;
            }

            var name = info.Name;
            var rel = CombineRelative(relative, name);
            side.Files[rel] = new FileEntry(rel, name, info.FullName, info.Length, info.LastWriteTimeUtc);
            if (relative.Length == 0)
            {
                side.RootImmediateFiles++;
            }
            else if (side.Folders.TryGetValue(relative, out var parent))
            {
                parent.ImmediateFiles++;
            }

            AddSubtreeFile(side, relative);
            filesVisited++;
            Pulse(progress, filesVisited, foldersVisited, rel, ref lastPulse);
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
            WaitIfPaused(pause, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            DirectoryInfo info;
            try
            {
                info = new DirectoryInfo(dir);
            }
            catch
            {
                continue;
            }

            if (SourceWalker.IsSkippedDirectoryName(info.Name))
            {
                continue;
            }

            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            var name = info.Name;
            var rel = CombineRelative(relative, name);
            side.Folders[rel] = new FolderEntry(rel, name);
            foldersVisited++;
            Pulse(progress, filesVisited, foldersVisited, rel, ref lastPulse);
            Walk(info.FullName, rel, side, cancellationToken, pause, progress, ref filesVisited, ref foldersVisited, ref lastPulse);
        }
    }

    private static void AddSubtreeFile(TreeSide side, string folderKey)
    {
        var current = folderKey;
        while (current.Length > 0)
        {
            if (side.Folders.TryGetValue(current, out var folder))
            {
                folder.SubtreeFiles++;
            }

            current = ParentKey(current);
        }
    }

    private static bool ShouldSkipFile(FileInfo info) =>
        info.Attributes.HasFlag(FileAttributes.ReparsePoint) &&
        !CloudPath.IsCloudPlaceholder(info.Attributes);

    private static string CombineRelative(string parent, string name) =>
        parent.Length == 0 ? name : parent + "\\" + name;

    private static string ParentKey(string relative)
    {
        var i = relative.LastIndexOf('\\');
        return i <= 0 ? "" : relative[..i];
    }

    private static void WaitIfPaused(PauseGate? pause, CancellationToken cancellationToken)
    {
        if (pause is null)
        {
            return;
        }

        while (pause.IsPaused)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Thread.Sleep(40);
        }
    }

    private static void Pulse(
        Action<DirectoryCompareProgress>? progress,
        int files,
        int folders,
        string? current,
        ref long lastPulse,
        bool force = false)
    {
        if (progress is null)
        {
            return;
        }

        if (!force && files % 50 != 0)
        {
            var elapsed = (Stopwatch.GetTimestamp() - lastPulse) / (double)Stopwatch.Frequency;
            if (elapsed < 0.2)
            {
                return;
            }
        }

        lastPulse = Stopwatch.GetTimestamp();
        progress(new DirectoryCompareProgress(files, folders, current));
    }

    private sealed class TreeSide
    {
        public Dictionary<string, FileEntry> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, FolderEntry> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int RootImmediateFiles { get; set; }
    }

    private sealed class FileEntry(string relative, string name, string fullPath, long size, DateTime lastWriteUtc)
    {
        public string Relative { get; } = relative;
        public string Name { get; } = name;
        public string FullPath { get; } = fullPath;
        public long Size { get; } = size;
        public DateTime LastWriteUtc { get; } = lastWriteUtc;
    }

    private sealed class FolderEntry(string relative, string name)
    {
        public string Relative { get; } = relative;
        public string Name { get; } = name;
        public int ImmediateFiles { get; set; }
        public int SubtreeFiles { get; set; }
    }
}

public static class DirectoryCompareReport
{
    public static string DefaultFileName(DateTime? local = null)
    {
        var stamp = local ?? DateTime.Now;
        return string.Create(CultureInfo.InvariantCulture, $"compare-{stamp:yyyyMMdd-HHmm}.txt");
    }

    public static string Build(DirectoryCompareResult result, CompareFilter filter)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Mercury compare report");
        sb.AppendLine("Generated: " + TransferRundown.FormatLogTime(DateTimeOffset.Now));
        sb.Append("Scan: ");
        if (result.Advanced)
        {
            sb.Append("Advanced (size, timestamp, name, files per folder");
            if (result.Hashed)
            {
                sb.Append(", hash xxHash64");
            }

            sb.Append(result.FatTimestampTolerance ? ", FAT 2s on)" : ", FAT 2s off)");
        }
        else
        {
            sb.Append("Default counts (no hash, no size/time compare)");
        }

        sb.AppendLine();
        sb.AppendLine("Left:  " + result.LeftRoot);
        sb.AppendLine("Right: " + result.RightRoot);
        sb.AppendLine("Started:  " + TransferRundown.FormatLogTime(result.StartedUtc));
        sb.AppendLine("Finished: " + TransferRundown.FormatLogTime(result.EndedUtc));
        if (result.Canceled)
        {
            sb.AppendLine("Status: canceled");
        }
        else if (!string.IsNullOrWhiteSpace(result.Error))
        {
            sb.AppendLine("Status: " + result.Error);
        }
        else
        {
            sb.AppendLine("Status: complete");
        }

        sb.AppendLine($"Visited: {result.FilesVisited} files, {result.FoldersVisited} folders");
        sb.AppendLine();
        sb.AppendLine("Folders");
        sb.AppendLine($"  Left {result.LeftFolders}  Right {result.RightFolders}  Only left {result.FoldersOnlyLeft}  Only right {result.FoldersOnlyRight}  Matching name {result.FoldersMatchingName}");
        sb.AppendLine("Files");
        sb.AppendLine($"  Left {result.LeftFiles}  Right {result.RightFiles}  Only left {result.FilesOnlyLeft}  Only right {result.FilesOnlyRight}  Same relative path {result.FilesSameRelativePath}");
        if (result.Advanced)
        {
            sb.AppendLine($"Size mismatches: {result.SizeMismatches}");
            sb.AppendLine($"Timestamp mismatches: {result.TimestampMismatches}");
            sb.AppendLine($"Hash mismatches: {result.HashMismatches}");
            sb.AppendLine($"Name issues: {result.NameMismatches}");
            sb.AppendLine($"Per-folder file-count mismatches: {result.FolderFileCountMismatches}");
        }

        void Section(string title, CompareDiffKind kind)
        {
            if (!DirectoryCompareDiff.IsIncluded(kind, filter))
            {
                return;
            }

            var items = result.Differences.Where(d => d.Kind == kind).ToList();
            sb.AppendLine();
            sb.AppendLine("=== " + title + " (" + items.Count + ") ===");
            if (items.Count == 0)
            {
                sb.AppendLine("(none)");
                return;
            }

            foreach (var item in items)
            {
                sb.AppendLine(string.IsNullOrEmpty(item.Detail) ? item.DisplayPath : item.DisplayPath + "  " + item.Detail);
            }
        }

        Section("Only left files", CompareDiffKind.OnlyLeftFile);
        Section("Only right files", CompareDiffKind.OnlyRightFile);
        Section("Only left folders", CompareDiffKind.OnlyLeftFolder);
        Section("Only right folders", CompareDiffKind.OnlyRightFolder);
        Section("Size mismatches", CompareDiffKind.SizeMismatch);
        Section("Timestamp mismatches", CompareDiffKind.TimestampMismatch);
        Section("Hash mismatches", CompareDiffKind.HashMismatch);
        Section("Name issues", CompareDiffKind.NameMismatch);
        Section("Per-folder file-count mismatches", CompareDiffKind.FolderFileCountMismatch);
        return sb.ToString();
    }

    public static void Write(string path, DirectoryCompareResult result, CompareFilter filter)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, Build(result, filter));
    }
}
