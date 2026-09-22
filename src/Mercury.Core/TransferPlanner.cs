using System.IO.Compression;

namespace Mercury;

public enum PackDisposition
{
    Stream,
    PackStored,
    PackDeflate,
    Sample
}

public sealed class PackPocket
{
    public string Id { get; init; } = "";
    public string ParentRelative { get; init; } = "";
    public List<FileRecord> Files { get; init; } = [];
    public PackDisposition Disposition { get; set; } = PackDisposition.Stream;
    public long TotalBytes => Files.Sum(f => f.Size);
}

public sealed class PackGroup
{
    public CopyMapping Mapping { get; init; } = new();
    public List<FileRecord> Files { get; } = [];
    public CompressionLevel Compression { get; set; } = CompressionLevel.NoCompression;
    public List<PackPocket> Pockets { get; } = [];
}

public sealed class TransferPlan
{
    public List<FileRecord> Stream { get; } = [];
    public List<PackGroup> PackGroups { get; } = [];
    public string Summary { get; set; } = "";
    public bool HasPacking => PackGroups.Any(g => g.Files.Count > 0);
}

/// <summary>
/// Builds the order of operations at job start: stream already-compressed / large files immediately,
/// pack small-file pockets (optionally after a 100–500 MB sample) without blocking the whole copy.
/// </summary>
public static class TransferPlanner
{
    public const long SmallFileBytes = 256 * 1024;
    public const long LargeStreamBytes = 4L * 1024 * 1024;
    public const long TinyAverageBytes = 64 * 1024;
    public const int MinPocketFiles = 8;
    public const int CertainTinyFiles = 24;
    public const long SampleBytes = 256L * 1024 * 1024;

    public static TransferPlan Build(
        Job job,
        IReadOnlyList<FileRecord> files,
        IReadOnlyList<CopyMapping> mappings,
        MagicPeekBudget? peek = null)
    {
        peek ??= MagicPeekBudget.ForPackSkip();
        var plan = new TransferPlan();
        var pending = files.Where(f => f.Status is not FileCopyStatus.Skipped).ToList();
        if (pending.Count == 0 || mappings.Count == 0)
        {
            plan.Summary = "Nothing to copy.";
            return plan;
        }

        if (job.Catcher is not null)
        {
            var group = new PackGroup { Mapping = mappings[0] };
            group.Files.AddRange(pending);
            plan.PackGroups.Add(group);
            plan.Summary = $"Catcher: pack {pending.Count} file(s) into one transport zip.";
            return plan;
        }

        var stream = new List<FileRecord>();
        var candidates = new List<FileRecord>();
        foreach (var file in pending)
        {
            if (PackPolicy.ShouldSkipPacking(file, job.Options, peek))
            {
                stream.Add(file);
                continue;
            }

            if (!job.Options.PackAsZip && file.Size >= LargeStreamBytes)
            {
                stream.Add(file);
                continue;
            }

            candidates.Add(file);
        }

        if (candidates.Count == 0)
        {
            plan.Stream.AddRange(stream);
            plan.Summary = FormatSummary(plan, forced: job.Options.PackAsZip);
            return plan;
        }

        if (job.Options.PackAsZip)
        {
            foreach (var mapping in mappings)
            {
                var groupFiles = candidates.Where(f => BelongsTo(f, mapping, mappings)).ToList();
                if (groupFiles.Count == 0)
                {
                    continue;
                }

                var group = new PackGroup { Mapping = mapping };
                group.Files.AddRange(groupFiles);
                plan.PackGroups.Add(group);
            }

            plan.Stream.AddRange(stream);
            plan.Summary = FormatSummary(plan, forced: true);
            return plan;
        }

        var pockets = GroupPockets(candidates);
        var packByMapping = mappings.ToDictionary(m => m.DestRoot, m => new PackGroup { Mapping = m }, StringComparer.OrdinalIgnoreCase);
        foreach (var pocket in pockets)
        {
            ClassifyPocket(pocket);
            if (pocket.Disposition is PackDisposition.Stream)
            {
                stream.AddRange(pocket.Files);
                continue;
            }

            var mapping = FindMapping(pocket.Files[0], mappings);
            if (!packByMapping.TryGetValue(mapping.DestRoot, out var group))
            {
                group = new PackGroup { Mapping = mapping };
                packByMapping[mapping.DestRoot] = group;
            }

            group.Pockets.Add(pocket);
            group.Files.AddRange(pocket.Files);
        }

        foreach (var group in packByMapping.Values)
        {
            if (group.Files.Count > 0)
            {
                plan.PackGroups.Add(group);
            }
        }

        plan.Stream.AddRange(stream);
        plan.Summary = FormatSummary(plan, forced: false);
        return plan;
    }

    public static void ApplySample(TransferPlan plan, PackPocket pocket, PackSampleResult result)
    {
        if (pocket.Disposition != PackDisposition.Sample)
        {
            return;
        }

        var group = plan.PackGroups.FirstOrDefault(g => g.Pockets.Contains(pocket));
        if (!result.Pack)
        {
            pocket.Disposition = PackDisposition.Stream;
            plan.Stream.AddRange(pocket.Files);
            if (group is not null)
            {
                foreach (var file in pocket.Files)
                {
                    group.Files.Remove(file);
                }

                group.Pockets.Remove(pocket);
                if (group.Files.Count == 0)
                {
                    plan.PackGroups.Remove(group);
                }
            }

            plan.Summary = FormatSummary(plan, forced: false);
            return;
        }

        pocket.Disposition = result.Deflate
            ? PackDisposition.PackDeflate
            : PackDisposition.PackStored;
        if (group is not null && result.Deflate)
        {
            group.Compression = CompressionLevel.Optimal;
        }

        plan.Summary = FormatSummary(plan, forced: false);
    }

    private static void ClassifyPocket(PackPocket pocket)
    {
        var count = pocket.Files.Count;
        var avg = count == 0 ? 0 : pocket.TotalBytes / count;
        if (count >= CertainTinyFiles && avg <= TinyAverageBytes)
        {
            pocket.Disposition = PackDisposition.PackStored;
            return;
        }

        if (count >= MinPocketFiles && pocket.Files.All(f => f.Size <= SmallFileBytes))
        {
            pocket.Disposition = count >= 16 && avg <= TinyAverageBytes
                ? PackDisposition.PackStored
                : PackDisposition.Sample;
            return;
        }

        if (count >= MinPocketFiles && avg <= SmallFileBytes)
        {
            pocket.Disposition = PackDisposition.Sample;
            return;
        }

        pocket.Disposition = PackDisposition.Stream;
    }

    private static List<PackPocket> GroupPockets(IReadOnlyList<FileRecord> candidates)
    {
        var groups = new Dictionary<string, PackPocket>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in candidates)
        {
            var parent = Path.GetDirectoryName(file.RelativePath) ?? "";
            if (!groups.TryGetValue(parent, out var pocket))
            {
                pocket = new PackPocket
                {
                    Id = string.IsNullOrEmpty(parent) ? "(root)" : parent,
                    ParentRelative = parent
                };
                groups[parent] = pocket;
            }

            pocket.Files.Add(file);
        }

        return groups.Values.ToList();
    }

    private static CopyMapping FindMapping(FileRecord file, IReadOnlyList<CopyMapping> mappings)
    {
        CopyMapping? best = null;
        var bestLen = -1;
        foreach (var mapping in mappings)
        {
            if (!BelongsTo(file, mapping, mappings))
            {
                continue;
            }

            if (mapping.DestRoot.Length > bestLen)
            {
                best = mapping;
                bestLen = mapping.DestRoot.Length;
            }
        }

        return best ?? mappings[0];
    }

    private static bool BelongsTo(FileRecord file, CopyMapping mapping, IReadOnlyList<CopyMapping> mappings)
    {
        try
        {
            var dest = Path.GetFullPath(file.DestPath);
            var root = Path.GetFullPath(mapping.DestRoot).TrimEnd('\\');
            var under = dest.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(Path.GetDirectoryName(dest)?.TrimEnd('\\'), root, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(dest.TrimEnd('\\'), root, StringComparison.OrdinalIgnoreCase);
            if (!under)
            {
                return file.SourcePath.StartsWith(mapping.SourceRoot.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(file.SourcePath, mapping.SourceRoot, StringComparison.OrdinalIgnoreCase)
                       || (mapping.SingleFile &&
                           string.Equals(file.SourcePath, Path.Combine(mapping.SourceRoot, mapping.SingleFileName ?? ""), StringComparison.OrdinalIgnoreCase));
            }

            foreach (var other in mappings)
            {
                if (ReferenceEquals(other, mapping) || other.DestRoot.Length <= mapping.DestRoot.Length)
                {
                    continue;
                }

                var otherRoot = Path.GetFullPath(other.DestRoot).TrimEnd('\\');
                if (dest.StartsWith(otherRoot + "\\", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetDirectoryName(dest)?.TrimEnd('\\'), otherRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return file.SourcePath.StartsWith(mapping.SourceRoot.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string FormatSummary(TransferPlan plan, bool forced)
    {
        var packFiles = plan.PackGroups.Sum(g => g.Files.Count);
        var stream = plan.Stream.Count;
        var sample = plan.PackGroups.SelectMany(g => g.Pockets).Count(p => p.Disposition == PackDisposition.Sample);
        var mode = forced ? "Small Files (force pack)" : "auto pockets";
        var sampleBit = sample > 0 ? $", {sample} pocket(s) will sample ~{ByteFormatter.ToString(SampleBytes)}" : "";
        return $"{mode}: stream {stream} file(s) now, pack {packFiles} file(s) in {plan.PackGroups.Count} archive(s){sampleBit}.";
    }
}
