using System.Diagnostics;
using System.Text;

namespace Mercury;

public sealed class PackSampleResult
{
    public bool Pack { get; init; }
    public bool Deflate { get; init; }
    public long RawBytes { get; init; }
    public long StoredBytes { get; init; }
    public long DeflateBytes { get; init; }
    public int Files { get; init; }
    public string Reason { get; init; } = "";

    public string FormatLine()
    {
        var stored = StoredBytes <= 0 || RawBytes <= 0 ? "n/a" : $"{100.0 * StoredBytes / RawBytes:0}% of raw";
        var deflate = DeflateBytes <= 0 || RawBytes <= 0 ? "n/a" : $"{100.0 * DeflateBytes / RawBytes:0}% of raw";
        return $"Pack sample ({Files} files, {ByteFormatter.ToString(RawBytes)}): stored {stored}, deflate {deflate} — {Reason}";
    }
}

/// <summary>
/// Optional 100–500 MB sample (default 256 MB, bounded by pocket size) to decide whether packing a pocket helps.
/// Prefers stored zip for lots of tiny files; uses deflate only when it clearly shrinks the sample.
/// </summary>
public static class PackSample
{
    public const long DefaultSampleBytes = 256L * 1024 * 1024;
    public const long MinSampleBytes = 100L * 1024 * 1024;
    public const long MaxSampleBytes = 500L * 1024 * 1024;

    public static long ClampSampleBytes(long requested, long pocketBytes)
    {
        var cap = Math.Clamp(requested, MinSampleBytes, MaxSampleBytes);
        if (pocketBytes <= 0)
        {
            return cap;
        }

        return Math.Min(cap, pocketBytes);
    }

    public static PackSampleResult Evaluate(
        IReadOnlyList<FileRecord> files,
        long sampleBytes = DefaultSampleBytes)
    {
        if (files.Count == 0)
        {
            return new PackSampleResult { Reason = "empty pocket — skip packing" };
        }

        var budget = ClampSampleBytes(sampleBytes, files.Sum(f => f.Size));
        var sample = new List<FileRecord>();
        var taken = 0L;
        foreach (var file in files.OrderBy(f => f.Size))
        {
            if (taken >= budget && sample.Count > 0)
            {
                break;
            }

            sample.Add(file);
            taken += file.Size;
        }

        var dir = Path.Combine(Path.GetTempPath(), "mercury-pack-sample-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var storedPath = Path.Combine(dir, "stored.zip");
        var deflatePath = Path.Combine(dir, "deflate.zip");
        try
        {
            var storedBytes = WriteSampleZip(storedPath, sample, CompressionLevel.NoCompression);
            var deflateBytes = WriteSampleZip(deflatePath, sample, CompressionLevel.Optimal);
            var raw = sample.Sum(f => f.Size);
            var avg = sample.Count == 0 ? 0 : raw / sample.Count;
            var deflateWins = deflateBytes > 0 && raw > 0 && deflateBytes < storedBytes * 0.85 && deflateBytes < raw * 0.85;
            var manyTiny = sample.Count >= TransferPlanner.MinPocketFiles && avg <= TransferPlanner.TinyAverageBytes;
            var pack = manyTiny || deflateWins || (sample.Count >= TransferPlanner.CertainTinyFiles);
            if (!pack && sample.Count >= TransferPlanner.MinPocketFiles && avg <= TransferPlanner.SmallFileBytes)
            {
                pack = true;
            }

            if (sample.Count < TransferPlanner.MinPocketFiles && !deflateWins)
            {
                pack = false;
            }

            var reason = !pack
                ? "no benefit — copy files as-is"
                : deflateWins
                    ? "deflate shrinks this pocket; pack with compression"
                    : "many small files — stored (uncompressed) zip for sequential write";

            return new PackSampleResult
            {
                Pack = pack,
                Deflate = pack && deflateWins,
                RawBytes = raw,
                StoredBytes = storedBytes,
                DeflateBytes = deflateBytes,
                Files = sample.Count,
                Reason = reason
            };
        }
        catch (Exception ex)
        {
            var many = files.Count >= TransferPlanner.CertainTinyFiles;
            return new PackSampleResult
            {
                Pack = many,
                Files = files.Count,
                RawBytes = files.Sum(f => f.Size),
                Reason = many
                    ? "sample failed (" + ex.Message + ") — packing stored zip anyway (many tiny files)"
                    : "sample failed (" + ex.Message + ") — skip packing"
            };
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
                // leftover temp is OK
            }
        }
    }

    private static long WriteSampleZip(string zipPath, IReadOnlyList<FileRecord> files, CompressionLevel level)
    {
        using (var zipStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: false))
        {
            var buffer = new byte[64 * 1024];
            for (var i = 0; i < files.Count; i++)
            {
                var file = files[i];
                if (!File.Exists(file.SourcePath))
                {
                    continue;
                }

                var entry = zip.CreateEntry(ZipPack.EntryName(file.RelativePath) + "-" + i, level);
                using var src = new FileStream(file.SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var dst = entry.Open();
                int read;
                while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                {
                    dst.Write(buffer, 0, read);
                }
            }
        }

        return new FileInfo(zipPath).Length;
    }
}
