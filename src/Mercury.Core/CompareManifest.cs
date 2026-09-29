using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mercury;

/// <summary>
/// File list for one Compare, plus each hash finished so far.
/// The count is written once. Each later hash is one appended line.
/// </summary>
public sealed class CompareManifest
{
    public string LeftRoot { get; set; } = "";
    public string RightRoot { get; set; } = "";
    public bool Advanced { get; set; }
    public bool Hash { get; set; }
    public bool FatTimestampTolerance { get; set; }
    public bool InventoryComplete { get; set; }
    /// <summary>Source file list is complete. Destination has not been inventoried yet.</summary>
    public bool SourceInventoryComplete { get; set; }
    public int LeftRootFiles { get; set; }
    public int RightRootFiles { get; set; }
    public List<CompareManifestEntry> Files { get; set; } = [];
    public List<CompareManifestFolder> Folders { get; set; } = [];

    public int SourceHashCount => Files.Count(file => file.Left && !string.IsNullOrEmpty(file.ContentHash));

    public int DestHashCount => Files.Count(file => !file.Left && !string.IsNullOrEmpty(file.ContentHash));

    public int ComparedFileCount
    {
        get
        {
            var source = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Files)
            {
                if (file.Left && !string.IsNullOrEmpty(file.ContentHash))
                {
                    source.Add(file.Relative);
                }
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var count = 0;
            foreach (var file in Files)
            {
                if (!file.Left && !string.IsNullOrEmpty(file.ContentHash) && source.Contains(file.Relative) && seen.Add(file.Relative))
                {
                    count++;
                }
            }

            return count;
        }
    }
}

public sealed class CompareManifestEntry
{
    public bool Left { get; set; }
    public string Relative { get; set; } = "";
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public long Size { get; set; }
    public long WriteTicks { get; set; }
    public string? ContentHash { get; set; }
}

public sealed class CompareManifestFolder
{
    public bool Left { get; set; }
    public string Relative { get; set; } = "";
    public string Name { get; set; } = "";
    public int ImmediateFiles { get; set; }
    public int SubtreeFiles { get; set; }
}

public static class CompareManifestStore
{
    public const string FileName = "compare-manifest.jsonl";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static CompareManifest? Load(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            CompareManifest? job = null;
            var files = new List<CompareManifestEntry>();
            var folders = new List<CompareManifestFolder>();
            var hashes = new Dictionary<string, HashMark>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                CompareManifestLine? row;
                try
                {
                    row = JsonSerializer.Deserialize<CompareManifestLine>(line, Json);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (row is null || string.IsNullOrEmpty(row.T))
                {
                    continue;
                }

                switch (row.T)
                {
                    case "job":
                        job = new CompareManifest
                        {
                            LeftRoot = row.Left ?? "",
                            RightRoot = row.Right ?? "",
                            Advanced = row.Advanced == true,
                            Hash = row.Hash == true,
                            FatTimestampTolerance = row.Fat == true
                        };
                        break;
                    case "file" when !string.IsNullOrEmpty(row.Rel):
                        files.Add(new CompareManifestEntry
                        {
                            Left = IsLeft(row.Side),
                            Relative = row.Rel,
                            Name = row.Name ?? "",
                            FullPath = row.Path ?? "",
                            Size = row.Size ?? 0,
                            WriteTicks = row.WriteTicks ?? 0
                        });
                        break;
                    case "folder" when !string.IsNullOrEmpty(row.Rel):
                        folders.Add(new CompareManifestFolder
                        {
                            Left = IsLeft(row.Side),
                            Relative = row.Rel,
                            Name = row.Name ?? "",
                            ImmediateFiles = row.Immediate ?? 0,
                            SubtreeFiles = row.Subtree ?? 0
                        });
                        break;
                    case "root" when job is not null:
                        if (IsLeft(row.Side))
                        {
                            job.LeftRootFiles = row.Immediate ?? 0;
                        }
                        else
                        {
                            job.RightRootFiles = row.Immediate ?? 0;
                        }

                        break;
                    case "inventory" when job is not null:
                        job.InventoryComplete = true;
                        break;
                    case "sourceInventory" when job is not null:
                        job.SourceInventoryComplete = true;
                        break;
                    case "hash" when !string.IsNullOrEmpty(row.Rel) && !string.IsNullOrEmpty(row.Digest):
                        hashes[Key(IsLeft(row.Side), row.Rel)] = new HashMark(row.Digest, row.Size ?? 0, row.WriteTicks ?? 0);
                        break;
                }
            }

            if (job is null)
            {
                return null;
            }

            foreach (var file in files)
            {
                if (hashes.TryGetValue(Key(file.Left, file.Relative), out var mark) &&
                    mark.Size == file.Size &&
                    mark.Ticks == file.WriteTicks)
                {
                    file.ContentHash = mark.Hash;
                }
            }

            job.Files = files;
            job.Folders = folders;
            return job;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string JobFile(string jobDirectory) => Path.Combine(jobDirectory, FileName);

    public static bool SameRoots(CompareManifest data, string left, string right)
    {
        try
        {
            return PathsEqual(data.LeftRoot, left) && PathsEqual(data.RightRoot, right);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Other jobs' manifests for this source → dest pair that already have source hashes.
    /// The compare file being written is skipped so it is not read while it is open for append.
    /// </summary>
    public static IReadOnlyList<CompareManifest> FindSourceHashSeeds(
        string jobsRoot,
        string left,
        string right,
        string? excludePath)
    {
        var seeds = new List<CompareManifest>();
        if (string.IsNullOrWhiteSpace(jobsRoot) || !Directory.Exists(jobsRoot))
        {
            return seeds;
        }

        string? exclude = null;
        if (!string.IsNullOrWhiteSpace(excludePath))
        {
            try
            {
                exclude = Path.GetFullPath(excludePath);
            }
            catch (ArgumentException)
            {
                exclude = excludePath;
            }
        }

        List<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(jobsRoot).ToList();
        }
        catch (IOException)
        {
            return seeds;
        }
        catch (UnauthorizedAccessException)
        {
            return seeds;
        }

        foreach (var dir in dirs)
        {
            var path = JobFile(dir);
            if (exclude is not null && FullPathsEqual(path, exclude))
            {
                continue;
            }

            var loaded = Load(path);
            if (loaded is null || !loaded.Hash || loaded.SourceHashCount == 0)
            {
                continue;
            }

            if (!SameRoots(loaded, left, right))
            {
                continue;
            }

            seeds.Add(loaded);
        }

        return seeds;
    }

    public static bool SameJob(CompareManifest data, string left, string right, bool advanced, bool hash, bool fat)
    {
        try
        {
            return PathsEqual(data.LeftRoot, left) &&
                   PathsEqual(data.RightRoot, right) &&
                   data.Advanced == advanced &&
                   data.Hash == hash &&
                   data.FatTimestampTolerance == fat;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static void Delete(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // the next Compare still appends; a locked file is retried then
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    internal static string Key(bool left, string relative) => (left ? "L\n" : "R\n") + relative;

    private static bool PathsEqual(string left, string right) =>
        string.Equals(PathNormalizer.Normalize(left), PathNormalizer.Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static bool FullPathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool IsLeft(string? side) => !string.Equals(side, "R", StringComparison.OrdinalIgnoreCase);

    internal readonly record struct HashMark(string Hash, long Size, long Ticks);
}

public sealed class CompareManifestWriter : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    private readonly Dictionary<string, CompareManifestStore.HashMark> _hashes;
    private bool _disposed;

    private CompareManifestWriter(
        StreamWriter writer,
        CompareManifest? inventory,
        Dictionary<string, CompareManifestStore.HashMark> hashes)
    {
        _writer = writer;
        Inventory = inventory;
        _hashes = hashes;
    }

    public CompareManifest? Inventory { get; private set; }

    public CompareManifest? SourceInventory { get; private set; }

    public bool InventoryComplete => Inventory is { InventoryComplete: true };

    public bool SourceInventoryComplete => SourceInventory is { SourceInventoryComplete: true };

    public static CompareManifestWriter Open(
        string path,
        string leftRoot,
        string rightRoot,
        bool advanced,
        bool hash,
        bool fat)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var loaded = CompareManifestStore.Load(path);
        var fullResume = loaded is { InventoryComplete: true } &&
                         CompareManifestStore.SameJob(loaded, leftRoot, rightRoot, advanced, hash, fat);
        var sourceResume = !fullResume &&
                           loaded is { SourceInventoryComplete: true } &&
                           CompareManifestStore.SameJob(loaded, leftRoot, rightRoot, advanced, hash, fat);
        var append = fullResume || sourceResume;
        var stream = new FileStream(
            path,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.Read);
        var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var hashes = new Dictionary<string, CompareManifestStore.HashMark>(StringComparer.OrdinalIgnoreCase);
        if (append && loaded is not null)
        {
            foreach (var file in loaded.Files)
            {
                if (string.IsNullOrEmpty(file.ContentHash))
                {
                    continue;
                }

                hashes[CompareManifestStore.Key(file.Left, file.Relative)] =
                    new CompareManifestStore.HashMark(file.ContentHash, file.Size, file.WriteTicks);
            }
        }

        var session = new CompareManifestWriter(writer, fullResume ? loaded : null, hashes);
        if (sourceResume)
        {
            session.SourceInventory = loaded;
        }

        if (!append)
        {
            session.WriteLine(new CompareManifestLine
            {
                T = "job",
                Left = leftRoot,
                Right = rightRoot,
                Advanced = advanced,
                Hash = hash,
                Fat = fat
            });
        }

        return session;
    }

    /// <summary>Source file list only. A later Compare still scans both trees and reuses the hashes.</summary>
    public void WriteSourceInventory(CompareManifest inventory)
    {
        lock (_gate)
        {
            if (_disposed || SourceInventoryComplete || InventoryComplete)
            {
                return;
            }

            foreach (var folder in inventory.Folders)
            {
                if (!folder.Left)
                {
                    continue;
                }

                WriteLine(new CompareManifestLine
                {
                    T = "folder",
                    Side = "L",
                    Rel = folder.Relative,
                    Name = folder.Name,
                    Immediate = folder.ImmediateFiles,
                    Subtree = folder.SubtreeFiles
                });
            }

            foreach (var file in inventory.Files)
            {
                if (!file.Left)
                {
                    continue;
                }

                WriteLine(new CompareManifestLine
                {
                    T = "file",
                    Side = "L",
                    Rel = file.Relative,
                    Name = file.Name,
                    Path = file.FullPath,
                    Size = file.Size,
                    WriteTicks = file.WriteTicks
                });
            }

            WriteLine(new CompareManifestLine { T = "root", Side = "L", Immediate = inventory.LeftRootFiles });
            WriteLine(new CompareManifestLine { T = "sourceInventory" });
            inventory.SourceInventoryComplete = true;
            SourceInventory = inventory;
        }
    }

    /// <summary>
    /// Copy source hashes that still match size and last-write ticks into this manifest.
    /// Destination hashes are left for the compare itself.
    /// </summary>
    public int SeedSourceHashes(CompareManifest prior)
    {
        if (Inventory is null)
        {
            return 0;
        }

        var seeded = 0;
        foreach (var file in prior.Files)
        {
            if (!file.Left || string.IsNullOrEmpty(file.ContentHash))
            {
                continue;
            }

            var live = Inventory.Files.FirstOrDefault(row =>
                row.Left && string.Equals(row.Relative, file.Relative, StringComparison.OrdinalIgnoreCase));
            if (live is null || live.Size != file.Size || live.WriteTicks != file.WriteTicks)
            {
                continue;
            }

            if (TryGetHash(true, file.Relative, file.Size, file.WriteTicks, out _))
            {
                continue;
            }

            WriteHash(true, file.Relative, file.ContentHash, file.Size, file.WriteTicks);
            seeded++;
        }

        return seeded;
    }

    public void WriteInventory(CompareManifest inventory)
    {
        lock (_gate)
        {
            if (_disposed || InventoryComplete)
            {
                return;
            }

            foreach (var folder in inventory.Folders)
            {
                WriteLine(new CompareManifestLine
                {
                    T = "folder",
                    Side = folder.Left ? "L" : "R",
                    Rel = folder.Relative,
                    Name = folder.Name,
                    Immediate = folder.ImmediateFiles,
                    Subtree = folder.SubtreeFiles
                });
            }

            foreach (var file in inventory.Files)
            {
                WriteLine(new CompareManifestLine
                {
                    T = "file",
                    Side = file.Left ? "L" : "R",
                    Rel = file.Relative,
                    Name = file.Name,
                    Path = file.FullPath,
                    Size = file.Size,
                    WriteTicks = file.WriteTicks
                });
            }

            WriteLine(new CompareManifestLine { T = "root", Side = "L", Immediate = inventory.LeftRootFiles });
            WriteLine(new CompareManifestLine { T = "root", Side = "R", Immediate = inventory.RightRootFiles });
            WriteLine(new CompareManifestLine { T = "inventory" });
            inventory.InventoryComplete = true;
            Inventory = inventory;
        }
    }

    public void WriteHash(bool left, string relative, string hash, long size, long writeTicks)
    {
        lock (_gate)
        {
            if (_disposed || string.IsNullOrEmpty(hash))
            {
                return;
            }

            WriteLine(new CompareManifestLine
            {
                T = "hash",
                Side = left ? "L" : "R",
                Rel = relative,
                Digest = hash,
                Size = size,
                WriteTicks = writeTicks
            });
            _hashes[CompareManifestStore.Key(left, relative)] = new CompareManifestStore.HashMark(hash, size, writeTicks);
        }
    }

    public bool TryGetHash(bool left, string relative, long size, long writeTicks, out string hash)
    {
        lock (_gate)
        {
            if (_hashes.TryGetValue(CompareManifestStore.Key(left, relative), out var mark) &&
                mark.Size == size &&
                mark.Ticks == writeTicks &&
                !string.IsNullOrEmpty(mark.Hash))
            {
                hash = mark.Hash;
                return true;
            }
        }

        hash = "";
        return false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writer.Flush();
            _writer.Dispose();
        }
    }

    private void WriteLine(CompareManifestLine line)
    {
        _writer.WriteLine(JsonSerializer.Serialize(line, Json));
        _writer.Flush();
        if (_writer.BaseStream is FileStream file)
        {
            file.Flush(flushToDisk: true);
        }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

internal sealed class CompareManifestLine
{
    public string T { get; set; } = "";
    public string? Left { get; set; }
    public string? Right { get; set; }
    public bool? Advanced { get; set; }
    public bool? Hash { get; set; }
    public bool? Fat { get; set; }
    public string? Side { get; set; }
    public string? Rel { get; set; }
    public string? Name { get; set; }
    public string? Path { get; set; }
    public long? Size { get; set; }
    public long? WriteTicks { get; set; }
    public int? Immediate { get; set; }
    public int? Subtree { get; set; }
    public string? Digest { get; set; }
}
