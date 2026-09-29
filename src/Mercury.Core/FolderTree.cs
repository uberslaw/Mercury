namespace Mercury;

public sealed class FolderTreeNode
{
    public string Name { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public int FileCount { get; init; }
    public int DirectFileCount { get; init; }
    public int SubdirCount { get; init; }
    public int DoneFiles { get; init; }
    public long TotalBytes { get; init; }
    public long DoneBytes { get; init; }
    public double Percent { get; init; }
    public string PercentText { get; init; } = "0%";
    public string DirectPercentText { get; init; } = "—";
    public string FilesText { get; init; } = "0";
    public string DirectFilesText { get; init; } = "0";
    public string SubdirsText { get; init; } = "0";
    public string EtaText { get; init; } = "—";
    public string DirectEtaText { get; init; } = "—";
    public IReadOnlyList<FolderTreeNode> Children { get; init; } = [];
    public bool IsLeaf => SubdirCount == 0;
}

/// <summary>
/// Folder-only tree from journal file rows (no file nodes).
/// FileCount / Percent / ETA are subtree totals (collapsed display).
/// DirectFileCount is files sitting in that folder only (expanded display).
/// </summary>
public static class FolderTree
{
    public static IReadOnlyList<FolderTreeNode> Build(
        IEnumerable<FileRecord> files,
        double bytesPerSecond,
        string? rootName = null)
    {
        var root = new Acc { Name = "", RelativePath = "" };
        foreach (var file in files)
        {
            Add(root, file);
        }

        if (root.Children.Count == 0)
        {
            if (root.Files == 0)
            {
                return [];
            }

            root.Name = string.IsNullOrWhiteSpace(rootName) ? "Source" : rootName;
            return [Freeze(root, bytesPerSecond)];
        }

        return root.Children.Values
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => Freeze(c, bytesPerSecond))
            .ToList();
    }

    private static void Add(Acc root, FileRecord file)
    {
        var rel = file.RelativePath.Replace('/', '\\').Trim('\\');
        if (string.IsNullOrEmpty(rel))
        {
            return;
        }

        var dir = Path.GetDirectoryName(rel) ?? "";
        var chain = new List<Acc> { root };
        var node = root;
        if (!string.IsNullOrEmpty(dir))
        {
            var path = "";
            foreach (var segment in dir.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                path = path.Length == 0 ? segment : path + "\\" + segment;
                if (!node.Children.TryGetValue(segment, out var child))
                {
                    child = new Acc { Name = segment, RelativePath = path };
                    node.Children[segment] = child;
                }

                node = child;
                chain.Add(node);
            }
        }

        var done = file.Status is FileCopyStatus.Copied or FileCopyStatus.Unpacked or FileCopyStatus.Skipped;
        foreach (var acc in chain)
        {
            acc.Files++;
            acc.Bytes += file.Size;
            if (done)
            {
                acc.DoneFiles++;
                acc.DoneBytes += file.Size;
            }
        }

        node.DirectFiles++;
        node.DirectBytes += file.Size;
        if (done)
        {
            node.DirectDoneFiles++;
            node.DirectDoneBytes += file.Size;
        }
    }

    private static FolderTreeNode Freeze(Acc acc, double bytesPerSecond)
    {
        var children = acc.Children.Values
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => Freeze(c, bytesPerSecond))
            .ToList();
        var subtree = Stats(acc.Bytes, acc.DoneBytes, acc.Files, acc.DoneFiles, bytesPerSecond);
        var direct = Stats(acc.DirectBytes, acc.DirectDoneBytes, acc.DirectFiles, acc.DirectDoneFiles, bytesPerSecond);
        return new FolderTreeNode
        {
            Name = acc.Name,
            RelativePath = acc.RelativePath,
            FileCount = acc.Files,
            DirectFileCount = acc.DirectFiles,
            SubdirCount = acc.Children.Count,
            DoneFiles = acc.DoneFiles,
            TotalBytes = acc.Bytes,
            DoneBytes = acc.DoneBytes,
            Percent = subtree.Percent,
            PercentText = subtree.PercentText,
            DirectPercentText = acc.DirectFiles == 0 ? "—" : direct.PercentText,
            FilesText = acc.Files.ToString(System.Globalization.CultureInfo.InvariantCulture),
            DirectFilesText = acc.DirectFiles.ToString(System.Globalization.CultureInfo.InvariantCulture),
            SubdirsText = acc.Children.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            EtaText = subtree.EtaText,
            DirectEtaText = acc.DirectFiles == 0 ? "—" : direct.EtaText,
            Children = children
        };
    }

    private static (double Percent, string PercentText, string EtaText) Stats(
        long bytes,
        long doneBytes,
        int files,
        int doneFiles,
        double bytesPerSecond)
    {
        var percent = bytes > 0
            ? 100.0 * doneBytes / bytes
            : files > 0 ? 100.0 * doneFiles / files : 0;
        var remaining = bytes - doneBytes;
        var eta = remaining <= 0 || bytesPerSecond < 1
            ? "—"
            : ByteFormatter.Eta(TimeSpan.FromSeconds(remaining / bytesPerSecond));
        return (percent, percent.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%", eta);
    }

    private sealed class Acc
    {
        public required string Name;
        public required string RelativePath;
        public int Files;
        public int DirectFiles;
        public int DoneFiles;
        public int DirectDoneFiles;
        public long Bytes;
        public long DirectBytes;
        public long DoneBytes;
        public long DirectDoneBytes;
        public Dictionary<string, Acc> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
