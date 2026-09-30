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

public enum CompareListTone
{
    Default,
    MissingOnDest,
    DestOnly
}

public sealed class DirectoryCompareOptions
{
    public static DirectoryCompareOptions Default { get; } = new();

    public bool Advanced { get; init; }
    public bool Hash { get; init; }
    /// <summary>Write the file list and source hashes. Destination hashes still run when <see cref="Hash"/> is on.</summary>
    public bool HashSource { get; init; }
    public bool FatTimestampTolerance { get; init; }
    public CompareFilter Filter { get; init; } = CompareFilter.FolderCounts | CompareFilter.FileCounts;
    public int MaxListedDifferences { get; init; } = 2_000;
    public int HighlightLimit { get; init; } = 5;
}

public sealed class DirectoryCompareProgress
{
    public DirectoryCompareProgress(int filesVisited, int foldersVisited, string? currentRelative, ComparePace? pace = null)
    {
        FilesVisited = filesVisited;
        FoldersVisited = foldersVisited;
        CurrentRelative = currentRelative;
        Pace = pace;
    }

    public int FilesVisited { get; }
    public int FoldersVisited { get; }
    public string? CurrentRelative { get; }
    public ComparePace? Pace { get; }
}

/// <summary>Bytes still to hash for the file, its folder, and the whole compare.</summary>
public sealed class ComparePace
{
    public string? CurrentFolder { get; init; }
    public long FileBytesDone { get; init; }
    public long FileBytesTotal { get; init; }
    public long FolderBytesDone { get; init; }
    public long FolderBytesTotal { get; init; }
    public long BytesDone { get; init; }
    public long BytesTotal { get; init; }
    public double BytesPerSecond { get; init; }

    public TimeSpan? FileEta => Estimate(FileBytesDone, FileBytesTotal, BytesPerSecond);
    public TimeSpan? FolderEta => Estimate(FolderBytesDone, FolderBytesTotal, BytesPerSecond);
    public TimeSpan? OverallEta => Estimate(BytesDone, BytesTotal, BytesPerSecond);

    public static TimeSpan? Estimate(long done, long total, double bytesPerSecond)
    {
        if (bytesPerSecond < 1 || total <= 0)
        {
            return null;
        }

        var remaining = total - Math.Max(0, done);
        if (remaining <= 0)
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.FromSeconds(remaining / bytesPerSecond);
    }

    public static string FileLabel(string? relative)
    {
        if (string.IsNullOrEmpty(relative))
        {
            return "—";
        }

        var slash = relative.LastIndexOf('\\');
        return slash < 0 ? relative : relative[(slash + 1)..];
    }

    public static string FolderLabel(string? relative)
    {
        if (string.IsNullOrEmpty(relative))
        {
            return "—";
        }

        var slash = relative.LastIndexOf('\\');
        return slash <= 0 ? "." : relative[..slash];
    }
}

/// <summary>Three header bars for a running compare: file, folder, and the whole compare.</summary>
public readonly record struct CompareHeaderBars(
    bool Indeterminate,
    double FilePercent,
    string FileText,
    double FolderPercent,
    string FolderText,
    double OverallPercent,
    string OverallText)
{
    public static CompareHeaderBars From(DirectoryCompareProgress? progress)
    {
        var relative = progress?.CurrentRelative;
        var fileName = ComparePace.FileLabel(relative);
        var pace = progress?.Pace;
        var folderName = string.IsNullOrEmpty(pace?.CurrentFolder)
            ? ComparePace.FolderLabel(relative)
            : pace!.CurrentFolder!;
        if (pace is not { BytesTotal: > 0 })
        {
            return new CompareHeaderBars(true, 0, fileName, 0, folderName, 0, "Compare  (counting)");
        }

        var filePct = Percent(pace.FileBytesDone, pace.FileBytesTotal);
        var folderPct = Percent(pace.FolderBytesDone, pace.FolderBytesTotal);
        var overallPct = Percent(pace.BytesDone, pace.BytesTotal);
        return new CompareHeaderBars(
            false,
            filePct,
            fileName + "  " + ProgressHeader.PercentLabel(filePct, pace.FileBytesTotal > 0),
            folderPct,
            folderName + "  " + ProgressHeader.PercentLabel(folderPct, pace.FolderBytesTotal > 0),
            overallPct,
            "Compare  " + ProgressHeader.PercentLabel(overallPct, true));
    }

    private static double Percent(long done, long total)
    {
        if (total <= 0)
        {
            return 0;
        }

        return Math.Clamp(100.0 * done / total, 0, 100);
    }
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
        CompareDiffKind.OnlyLeftFile => "source only · file",
        CompareDiffKind.OnlyRightFile => "dest only · file",
        CompareDiffKind.OnlyLeftFolder => "source only · folder",
        CompareDiffKind.OnlyRightFolder => "dest only · folder",
        CompareDiffKind.SizeMismatch => "size",
        CompareDiffKind.TimestampMismatch => "timestamp",
        CompareDiffKind.HashMismatch => "hash",
        CompareDiffKind.NameMismatch => "name",
        CompareDiffKind.FolderFileCountMismatch => "files in folder",
        _ => Kind.ToString()
    };

    public CompareListTone ListTone => Kind switch
    {
        CompareDiffKind.OnlyLeftFile or CompareDiffKind.OnlyLeftFolder => CompareListTone.MissingOnDest,
        CompareDiffKind.OnlyRightFile or CompareDiffKind.OnlyRightFolder => CompareListTone.DestOnly,
        _ => CompareListTone.Default
    };

    public bool IsMissingOnDest => ListTone == CompareListTone.MissingOnDest;
    public bool IsDestOnly => ListTone == CompareListTone.DestOnly;

    public string ListForegroundKey => ListTone switch
    {
        CompareListTone.MissingOnDest => "CompareMissingOnDestBrush",
        CompareListTone.DestOnly => "CompareDestOnlyBrush",
        _ => "TextBrush"
    };

    public static bool IsDestOnlyKind(CompareDiffKind kind) =>
        kind is CompareDiffKind.OnlyRightFile or CompareDiffKind.OnlyRightFolder;

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
    public bool IsMissingOnDest { get; init; }
    public bool IsDestOnly { get; init; }
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

    public IEnumerable<DirectoryCompareDiff> Filtered(CompareFilter filter, bool includeDestOnly = true) =>
        Differences.Where(d =>
            DirectoryCompareDiff.IsIncluded(d.Kind, filter) &&
            (includeDestOnly || !DirectoryCompareDiff.IsDestOnlyKind(d.Kind)));

    public IReadOnlyList<DirectoryCompareDiff> Listed(CompareFilter filter, int cap, bool includeDestOnly = true)
    {
        var rows = Filtered(filter, includeDestOnly);
        if (cap <= 0)
        {
            return rows.ToList();
        }

        return rows.Take(cap).ToList();
    }

    public IReadOnlyList<DirectoryCompareHighlight> Highlights(CompareFilter filter, int limit, bool includeDestOnly = true)
    {
        limit = Math.Max(1, limit);
        var cards = new List<DirectoryCompareHighlight>();
        var diffs = Differences;

        void AddCard(string title, IEnumerable<string> lines, bool missingOnDest = false, bool destOnly = false)
        {
            if (destOnly && !includeDestOnly)
            {
                return;
            }

            var list = lines.Take(limit).ToList();
            if (list.Count > 0)
            {
                cards.Add(new DirectoryCompareHighlight
                {
                    Title = title,
                    Lines = list,
                    IsMissingOnDest = missingOnDest,
                    IsDestOnly = destOnly
                });
            }
        }

        if (filter.HasFlag(CompareFilter.FolderCounts))
        {
            AddCard(
                "Source-only folders",
                diffs.Where(d => d.Kind == CompareDiffKind.OnlyLeftFolder)
                    .OrderByDescending(d => d.LeftFileCount ?? 0)
                    .ThenBy(d => d.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .Select(d => FolderLine(d, left: true)),
                missingOnDest: true);
            AddCard(
                "Destination-only folders",
                diffs.Where(d => d.Kind == CompareDiffKind.OnlyRightFolder)
                    .OrderByDescending(d => d.RightFileCount ?? 0)
                    .ThenBy(d => d.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .Select(d => FolderLine(d, left: false)),
                destOnly: true);
        }

        if (filter.HasFlag(CompareFilter.FileCounts))
        {
            AddCard(
                "Source-only files",
                diffs.Where(d => d.Kind == CompareDiffKind.OnlyLeftFile)
                    .OrderByDescending(d => d.LeftSize ?? 0)
                    .ThenBy(d => d.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .Select(FileLine),
                missingOnDest: true);
            AddCard(
                "Destination-only files",
                diffs.Where(d => d.Kind == CompareDiffKind.OnlyRightFile)
                    .OrderByDescending(d => d.RightSize ?? 0)
                    .ThenBy(d => d.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .Select(FileLine),
                destOnly: true);
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
        Action<DirectoryCompareProgress>? progress = null,
        string? manifestPath = null,
        string? seedJobsRoot = null)
    {
        options ??= DirectoryCompareOptions.Default;
        var started = DateTimeOffset.UtcNow;
        CompareManifestWriter? manifest = null;
        try
        {
            var leftPath = PathNormalizer.Normalize(leftRoot);
            var rightPath = PathNormalizer.Normalize(rightRoot);
            if (!Directory.Exists(leftPath))
            {
                return Failed(leftPath, rightRoot, started, $"Source folder not found: {leftPath}");
            }

            if (!Directory.Exists(rightPath))
            {
                return Failed(leftPath, rightPath, started, $"Destination folder not found: {rightPath}");
            }

            var advanced = options.Advanced;
            var hashDest = options.Hash && advanced;
            var hashSource = options.HashSource || hashDest;
            if (hashSource && !string.IsNullOrWhiteSpace(manifestPath))
            {
                manifest = CompareManifestWriter.Open(
                    manifestPath,
                    leftPath,
                    rightPath,
                    advanced,
                    hash: true,
                    options.FatTimestampTolerance);
            }

            var filesVisited = 0;
            var foldersVisited = 0;
            var lastPulse = Stopwatch.GetTimestamp();
            TreeSide left;
            TreeSide right;
            if (manifest is { InventoryComplete: true, Inventory: { } saved })
            {
                left = FromManifest(saved, leftSide: true);
                right = FromManifest(saved, leftSide: false);
                filesVisited = left.Files.Count + right.Files.Count;
                foldersVisited = left.Folders.Count + right.Folders.Count;
            }
            else
            {
                left = ScanSide(leftPath, cancellationToken, pause, progress, ref filesVisited, ref foldersVisited, ref lastPulse);
                right = ScanSide(rightPath, cancellationToken, pause, progress, ref filesVisited, ref foldersVisited, ref lastPulse);
                manifest?.WriteInventory(Snapshot(left, right));
            }

            if (hashSource && manifest is not null && !string.IsNullOrWhiteSpace(seedJobsRoot))
            {
                foreach (var seed in CompareManifestStore.FindSourceHashSeeds(seedJobsRoot, leftPath, rightPath, manifestPath))
                {
                    manifest.SeedSourceHashes(seed);
                }
            }

            lastPulse = Pulse(progress, filesVisited, foldersVisited, null, lastPulse, force: true);

            var diffs = new List<DirectoryCompareDiff>();
            var tolerance = options.FatTimestampTolerance
                ? CopyEngine.FatTimestampTolerance
                : TimeSpan.Zero;

            CompareFolders(left, right, advanced, diffs);
            CompareFiles(left, right, advanced, hashDest, hashSource, tolerance, diffs, cancellationToken, pause, progress, ref filesVisited, ref foldersVisited, ref lastPulse, manifest);

            var result = BuildResult(leftPath, rightPath, started, DateTimeOffset.UtcNow, options, left, right, diffs, filesVisited, foldersVisited, canceled: false, error: null);
            lastPulse = Pulse(progress, filesVisited, foldersVisited, null, lastPulse, force: true);
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
        finally
        {
            manifest?.Dispose();
        }
    }

    /// <summary>
    /// Hash source files into a compare manifest while a copy runs.
    /// Inventory is source-only, so a later Compare still walks both trees and only skips source hashes
    /// whose size and last-write ticks still match. Destination files are not hashed here.
    /// </summary>
    public static void RecordSourceHashes(
        string sourceRoot,
        string destRoot,
        string manifestPath,
        bool fatTimestampTolerance,
        CancellationToken cancellationToken = default,
        PauseGate? pause = null)
    {
        var sourcePath = PathNormalizer.Normalize(sourceRoot);
        var destPath = PathNormalizer.Normalize(destRoot);
        if (!Directory.Exists(sourcePath))
        {
            throw new DirectoryNotFoundException("Source folder not found: " + sourcePath);
        }

        using var manifest = CompareManifestWriter.Open(
            manifestPath,
            sourcePath,
            destPath,
            advanced: true,
            hash: true,
            fatTimestampTolerance);
        TreeSide source;
        if (manifest.InventoryComplete && manifest.Inventory is { } full)
        {
            source = FromManifest(full, leftSide: true);
        }
        else if (manifest.SourceInventoryComplete && manifest.SourceInventory is { } saved)
        {
            source = FromManifest(saved, leftSide: true);
        }
        else
        {
            var filesVisited = 0;
            var foldersVisited = 0;
            var lastPulse = Stopwatch.GetTimestamp();
            source = ScanSide(sourcePath, cancellationToken, pause, progress: null, ref filesVisited, ref foldersVisited, ref lastPulse);
            manifest.WriteSourceInventory(SnapshotSource(source));
        }

        foreach (var file in source.Files.Values.OrderBy(entry => entry.Relative, StringComparer.OrdinalIgnoreCase))
        {
            WaitIfPaused(pause, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (manifest.TryGetHash(true, file.Relative, file.Size, file.LastWriteUtc.Ticks, out _))
            {
                continue;
            }

            FileInfo info;
            try
            {
                info = new FileInfo(file.FullPath);
            }
            catch
            {
                continue;
            }

            if (!info.Exists || info.Length != file.Size || info.LastWriteTimeUtc.Ticks != file.LastWriteUtc.Ticks)
            {
                continue;
            }

            var hash = HashUtil.HashFile(
                info.FullName,
                cancellationToken,
                _ => WaitIfPaused(pause, cancellationToken));
            manifest.WriteHash(true, file.Relative, hash, file.Size, file.LastWriteUtc.Ticks);
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
        bool hashDest,
        bool hashSource,
        TimeSpan tolerance,
        List<DirectoryCompareDiff> diffs,
        CancellationToken cancellationToken,
        PauseGate? pause,
        Action<DirectoryCompareProgress>? progress,
        ref int filesVisited,
        ref int foldersVisited,
        ref long lastPulse,
        CompareManifestWriter? manifest)
    {
        var keys = left.Files.Keys.ToList();
        keys.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            var file = left.Files[key];
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

        if (hashDest)
        {
            lastPulse = HashContents(
                left,
                right,
                diffs,
                cancellationToken,
                pause,
                progress,
                filesVisited,
                foldersVisited,
                lastPulse,
                manifest);
        }
        else if (hashSource)
        {
            lastPulse = HashSourceOnly(
                left,
                cancellationToken,
                pause,
                progress,
                filesVisited,
                foldersVisited,
                lastPulse,
                manifest);
        }
    }

    /// <summary>
    /// Hash source files only. The file list and each finished source hash stay in the manifest
    /// so a later content compare can skip them and hash the destination.
    /// </summary>
    private static long HashSourceOnly(
        TreeSide left,
        CancellationToken cancellationToken,
        PauseGate? pause,
        Action<DirectoryCompareProgress>? progress,
        int filesVisited,
        int foldersVisited,
        long lastPulse,
        CompareManifestWriter? manifest)
    {
        var files = left.Files.Values.OrderBy(file => file.Relative, StringComparer.OrdinalIgnoreCase).ToList();
        long total = 0;
        foreach (var file in files)
        {
            total += file.Size;
        }

        var clock = new HashClock();
        var credited = 0L;
        var pulseAt = lastPulse;
        void Emit(string current, long fileDone, long fileTotal, bool force = false)
        {
            pulseAt = Pulse(progress, filesVisited, foldersVisited, current, pulseAt, new ComparePace
            {
                CurrentFolder = string.IsNullOrEmpty(current) ? "." : ParentKey(current),
                FileBytesDone = fileDone,
                FileBytesTotal = fileTotal,
                BytesDone = credited + clock.Bytes,
                BytesTotal = Math.Max(total, 1),
                BytesPerSecond = clock.BytesPerSecond
            }, force);
        }

        foreach (var file in files)
        {
            WaitIfPaused(pause, cancellationToken, clock);
            cancellationToken.ThrowIfCancellationRequested();
            if (SavedHash(manifest, leftSide: true, file, out _))
            {
                credited += file.Size;
                continue;
            }

            var fileDone = 0L;
            void OnRead(long read)
            {
                WaitIfPaused(pause, cancellationToken, clock);
                clock.Add(read);
                fileDone += read;
                Emit(file.Relative, fileDone, file.Size);
            }

            Emit(file.Relative, 0, file.Size, force: true);
            try
            {
                var value = HashUtil.HashFile(file.FullPath, cancellationToken, OnRead);
                manifest?.WriteHash(true, file.Relative, value, file.Size, file.LastWriteUtc.Ticks);
                credited += file.Size;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // a source file that cannot be read is left unhashed; the compare still finishes
                _ = ex;
            }

            Emit(file.Relative, file.Size, file.Size, force: true);
        }

        return pulseAt;
    }

    /// <summary>
    /// Hash every source file, then every destination file. Finished hashes are appended
    /// before the end-of-file pulse so a cancel keeps that file.
    /// </summary>
    private static long HashContents(
        TreeSide left,
        TreeSide right,
        List<DirectoryCompareDiff> diffs,
        CancellationToken cancellationToken,
        PauseGate? pause,
        Action<DirectoryCompareProgress>? progress,
        int filesVisited,
        int foldersVisited,
        long lastPulse,
        CompareManifestWriter? manifest)
    {
        var folderTotals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long hashTotal = 0;
        var keys = left.Files.Keys.ToList();
        keys.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            var entry = left.Files[key];
            if (!right.Files.TryGetValue(key, out var match) || entry.Size != match.Size)
            {
                continue;
            }

            var bytes = entry.Size + match.Size;
            hashTotal += bytes;
            var folderKey = ParentKey(key);
            folderTotals[folderKey] = folderTotals.GetValueOrDefault(folderKey) + bytes;
        }

        if (hashTotal <= 0)
        {
            return lastPulse;
        }

        var clock = new HashClock();
        var credited = 0L;
        var savedFolder = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var leftDone = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rightDone = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            var file = left.Files[key];
            if (!right.Files.TryGetValue(key, out var other) || file.Size != other.Size)
            {
                continue;
            }

            var folder = ParentKey(key);
            if (SavedHash(manifest, leftSide: true, file, out var leftHash))
            {
                leftDone[key] = leftHash;
                credited += file.Size;
                savedFolder[folder] = savedFolder.GetValueOrDefault(folder) + file.Size;
            }

            if (SavedHash(manifest, leftSide: false, other, out var rightHash))
            {
                rightDone[key] = rightHash;
                credited += other.Size;
                savedFolder[folder] = savedFolder.GetValueOrDefault(folder) + other.Size;
            }
        }

        string? openFolder = null;
        var folderDone = 0L;
        var folderTotal = 0L;
        var pulseAt = lastPulse;
        void Emit(string current, long fileDone, long fileTotal, bool force = false)
        {
            pulseAt = Pulse(progress, filesVisited, foldersVisited, current, pulseAt, new ComparePace
            {
                CurrentFolder = string.IsNullOrEmpty(openFolder) ? "." : openFolder,
                FileBytesDone = fileDone,
                FileBytesTotal = fileTotal,
                FolderBytesDone = folderDone,
                FolderBytesTotal = folderTotal,
                BytesDone = credited + clock.Bytes,
                BytesTotal = hashTotal,
                BytesPerSecond = clock.BytesPerSecond
            }, force);
        }

        if (credited > 0)
        {
            Emit("", 0, 0, force: true);
        }

        void HashSide(bool leftSide)
        {
            openFolder = null;
            var done = leftSide ? leftDone : rightDone;
            foreach (var key in keys)
            {
                var file = left.Files[key];
                if (!right.Files.TryGetValue(key, out var other) || file.Size != other.Size || failed.Contains(key))
                {
                    continue;
                }

                WaitIfPaused(pause, cancellationToken, clock);
                cancellationToken.ThrowIfCancellationRequested();
                var entry = leftSide ? file : other;
                var folder = ParentKey(key);
                if (!string.Equals(folder, openFolder, StringComparison.OrdinalIgnoreCase))
                {
                    openFolder = folder;
                    folderDone = savedFolder.GetValueOrDefault(folder);
                    folderTotal = folderTotals.GetValueOrDefault(folder);
                }

                if (done.ContainsKey(key))
                {
                    continue;
                }

                var fileTotal = entry.Size;
                var fileDone = 0L;
                void OnRead(long read)
                {
                    WaitIfPaused(pause, cancellationToken, clock);
                    clock.Add(read);
                    fileDone += read;
                    folderDone += read;
                    Emit(key, fileDone, fileTotal);
                }

                Emit(key, 0, fileTotal, force: true);
                try
                {
                    var value = HashUtil.HashFile(entry.FullPath, cancellationToken, OnRead);
                    manifest?.WriteHash(leftSide, key, value, entry.Size, entry.LastWriteUtc.Ticks);
                    savedFolder[folder] = savedFolder.GetValueOrDefault(folder) + entry.Size;
                    done[key] = value;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed.Add(key);
                    diffs.Add(new DirectoryCompareDiff
                    {
                        Kind = CompareDiffKind.HashMismatch,
                        RelativePath = key,
                        LeftSize = file.Size,
                        RightSize = other.Size,
                        Detail = ex.Message
                    });
                }

                Emit(key, fileTotal, fileTotal, force: true);
            }
        }

        HashSide(leftSide: true);
        HashSide(leftSide: false);
        foreach (var key in keys)
        {
            var file = left.Files[key];
            if (!right.Files.TryGetValue(key, out var other) || file.Size != other.Size || failed.Contains(key))
            {
                continue;
            }

            if (!leftDone.TryGetValue(key, out var leftHash) || !rightDone.TryGetValue(key, out var rightHash))
            {
                continue;
            }

            if (string.Equals(leftHash, rightHash, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            diffs.Add(new DirectoryCompareDiff
            {
                Kind = CompareDiffKind.HashMismatch,
                RelativePath = key,
                LeftSize = file.Size,
                RightSize = other.Size,
                Detail = $"{leftHash} vs {rightHash}"
            });
        }

        return pulseAt;
    }

    private static bool SavedHash(CompareManifestWriter? manifest, bool leftSide, FileEntry file, out string hash)
    {
        hash = "";
        if (manifest is null || !manifest.TryGetHash(leftSide, file.Relative, file.Size, file.LastWriteUtc.Ticks, out hash))
        {
            return false;
        }

        try
        {
            var info = new FileInfo(file.FullPath);
            if (info.Length != file.Size || info.LastWriteTimeUtc.Ticks != file.LastWriteUtc.Ticks)
            {
                hash = "";
                return false;
            }
        }
        catch
        {
            hash = "";
            return false;
        }

        return true;
    }

    private static CompareManifest Snapshot(TreeSide left, TreeSide right)
    {
        var manifest = new CompareManifest
        {
            LeftRootFiles = left.RootImmediateFiles,
            RightRootFiles = right.RootImmediateFiles
        };
        foreach (var folder in left.Folders.Values)
        {
            manifest.Folders.Add(new CompareManifestFolder
            {
                Left = true,
                Relative = folder.Relative,
                Name = folder.Name,
                ImmediateFiles = folder.ImmediateFiles,
                SubtreeFiles = folder.SubtreeFiles
            });
        }

        foreach (var folder in right.Folders.Values)
        {
            manifest.Folders.Add(new CompareManifestFolder
            {
                Left = false,
                Relative = folder.Relative,
                Name = folder.Name,
                ImmediateFiles = folder.ImmediateFiles,
                SubtreeFiles = folder.SubtreeFiles
            });
        }

        foreach (var file in left.Files.Values)
        {
            manifest.Files.Add(ToEntry(file, left: true));
        }

        foreach (var file in right.Files.Values)
        {
            manifest.Files.Add(ToEntry(file, left: false));
        }

        return manifest;
    }

    private static CompareManifest SnapshotSource(TreeSide source)
    {
        var manifest = new CompareManifest
        {
            LeftRootFiles = source.RootImmediateFiles
        };
        foreach (var folder in source.Folders.Values)
        {
            manifest.Folders.Add(new CompareManifestFolder
            {
                Left = true,
                Relative = folder.Relative,
                Name = folder.Name,
                ImmediateFiles = folder.ImmediateFiles,
                SubtreeFiles = folder.SubtreeFiles
            });
        }

        foreach (var file in source.Files.Values)
        {
            manifest.Files.Add(ToEntry(file, left: true));
        }

        return manifest;
    }

    private static CompareManifestEntry ToEntry(FileEntry file, bool left) =>
        new()
        {
            Left = left,
            Relative = file.Relative,
            Name = file.Name,
            FullPath = file.FullPath,
            Size = file.Size,
            WriteTicks = file.LastWriteUtc.Ticks
        };

    private static TreeSide FromManifest(CompareManifest manifest, bool leftSide)
    {
        var side = new TreeSide
        {
            RootImmediateFiles = leftSide ? manifest.LeftRootFiles : manifest.RightRootFiles
        };
        foreach (var folder in manifest.Folders)
        {
            if (folder.Left != leftSide)
            {
                continue;
            }

            side.Folders[folder.Relative] = new FolderEntry(folder.Relative, folder.Name)
            {
                ImmediateFiles = folder.ImmediateFiles,
                SubtreeFiles = folder.SubtreeFiles
            };
        }

        foreach (var file in manifest.Files)
        {
            if (file.Left != leftSide)
            {
                continue;
            }

            side.Files[file.Relative] = new FileEntry(
                file.Relative,
                file.Name,
                file.FullPath,
                file.Size,
                new DateTime(file.WriteTicks, DateTimeKind.Utc));
        }

        return side;
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
            lastPulse = Pulse(progress, filesVisited, foldersVisited, rel, lastPulse);
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
            lastPulse = Pulse(progress, filesVisited, foldersVisited, rel, lastPulse);
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

    private static void WaitIfPaused(PauseGate? pause, CancellationToken cancellationToken, HashClock? clock = null)
    {
        if (pause is null || !pause.IsEffectivelyPaused)
        {
            return;
        }

        clock?.Pause();
        try
        {
            while (pause.IsEffectivelyPaused)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Thread.Sleep(40);
            }
        }
        finally
        {
            clock?.Resume();
        }
    }

    private static long Pulse(
        Action<DirectoryCompareProgress>? progress,
        int files,
        int folders,
        string? current,
        long lastPulse,
        ComparePace? pace = null,
        bool force = false)
    {
        if (progress is null)
        {
            return lastPulse;
        }

        if (!force && files % 50 != 0)
        {
            var elapsed = (Stopwatch.GetTimestamp() - lastPulse) / (double)Stopwatch.Frequency;
            if (elapsed < 0.2)
            {
                return lastPulse;
            }
        }

        progress(new DirectoryCompareProgress(files, folders, current, pace));
        return Stopwatch.GetTimestamp();
    }

    private sealed class HashClock
    {
        private long _bytes;
        private long _runStart;
        private long _runTicks;
        private bool _running;

        public long Bytes => _bytes;

        public void Add(long count)
        {
            if (count <= 0)
            {
                return;
            }

            if (!_running)
            {
                _runStart = Stopwatch.GetTimestamp();
                _running = true;
            }

            _bytes += count;
        }

        public void Pause()
        {
            if (!_running)
            {
                return;
            }

            _runTicks += Stopwatch.GetTimestamp() - _runStart;
            _running = false;
        }

        public void Resume()
        {
            if (_bytes == 0 || _running)
            {
                return;
            }

            _runStart = Stopwatch.GetTimestamp();
            _running = true;
        }

        public double BytesPerSecond
        {
            get
            {
                var ticks = _runTicks;
                if (_running)
                {
                    ticks += Stopwatch.GetTimestamp() - _runStart;
                }

                var seconds = ticks / (double)Stopwatch.Frequency;
                return seconds >= 1 && _bytes > 0 ? _bytes / seconds : 0;
            }
        }
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
        sb.AppendLine("Source:      " + result.LeftRoot);
        sb.AppendLine("Destination: " + result.RightRoot);
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
        sb.AppendLine($"  Source {result.LeftFolders}  Destination {result.RightFolders}  Source only {result.FoldersOnlyLeft}  Dest only {result.FoldersOnlyRight}  Matching name {result.FoldersMatchingName}");
        sb.AppendLine("Files");
        sb.AppendLine($"  Source {result.LeftFiles}  Destination {result.RightFiles}  Source only {result.FilesOnlyLeft}  Dest only {result.FilesOnlyRight}  Same relative path {result.FilesSameRelativePath}");
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

        Section("Source-only files (missing on destination)", CompareDiffKind.OnlyLeftFile);
        Section("Destination-only files", CompareDiffKind.OnlyRightFile);
        Section("Source-only folders (missing on destination)", CompareDiffKind.OnlyLeftFolder);
        Section("Destination-only folders", CompareDiffKind.OnlyRightFolder);
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
