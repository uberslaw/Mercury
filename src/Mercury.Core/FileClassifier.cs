namespace Mercury;

public enum PayloadKind
{
    Other = 0,
    Video = 1,
    Audio = 2,
    Image = 3,
    Archive = 4,
    DiskImage = 5
}

/// <summary>
/// Classifies files by extension, with a cheap magic-byte peek for a few unknown names.
/// Shared by pack-as-zip skip and unbuffered auto-probe.
/// </summary>
public static class FileClassifier
{
    private static readonly HashSet<string> Video = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".mov", ".avi", ".webm", ".wmv", ".m2ts", ".mts", ".ts",
        ".mpg", ".mpeg", ".vob", ".flv", ".3gp", ".m2v", ".ogv"
    };

    private static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".aac", ".m4a", ".ogg", ".opus", ".flac", ".wma", ".m4b"
    };

    private static readonly HashSet<string> Image = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".png", ".gif", ".webp", ".heic", ".heif", ".jxl", ".avif"
    };

    private static readonly HashSet<string> Archive = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".gz", ".bz2", ".xz", ".tar", ".tgz", ".tbz", ".tbz2",
        ".cab", ".zst", ".lz", ".lzma"
    };

    private static readonly HashSet<string> DiskImage = new(StringComparer.OrdinalIgnoreCase)
    {
        ".iso", ".vhd", ".vhdx", ".img"
    };

    public static PayloadKind FromExtension(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return PayloadKind.Other;
        }

        var ext = Path.GetExtension(path);
        if (ext.Length == 0)
        {
            return PayloadKind.Other;
        }

        if (Video.Contains(ext))
        {
            return PayloadKind.Video;
        }

        if (Audio.Contains(ext))
        {
            return PayloadKind.Audio;
        }

        if (Image.Contains(ext))
        {
            return PayloadKind.Image;
        }

        if (DiskImage.Contains(ext))
        {
            return PayloadKind.DiskImage;
        }

        if (Archive.Contains(ext))
        {
            return PayloadKind.Archive;
        }

        return PayloadKind.Other;
    }

    public static PayloadKind Classify(string? path, long size = 0, MagicPeekBudget? peek = null)
    {
        var fromExt = FromExtension(path);
        if (fromExt != PayloadKind.Other)
        {
            return fromExt;
        }

        if (peek is null || !peek.TryConsumeUnknown(size) || string.IsNullOrWhiteSpace(path))
        {
            return PayloadKind.Other;
        }

        return FromMagic(path) ?? PayloadKind.Other;
    }

    public static bool IsAlreadyCompressed(string? path) =>
        IsAlreadyCompressed(FromExtension(path));

    public static bool IsAlreadyCompressed(PayloadKind kind) =>
        kind is PayloadKind.Video or PayloadKind.Audio or PayloadKind.Image
            or PayloadKind.Archive or PayloadKind.DiskImage;

    public static bool IsAlreadyCompressed(FileRecord file, MagicPeekBudget? peek = null)
    {
        if (file.PayloadKind != PayloadKind.Other && IsAlreadyCompressed(file.PayloadKind))
        {
            return true;
        }

        if (IsAlreadyCompressed(file.RelativePath) || IsAlreadyCompressed(file.SourcePath))
        {
            return true;
        }

        return IsAlreadyCompressed(Classify(file.SourcePath, file.Size, peek));
    }

    public static bool IsSequentialKind(PayloadKind kind) =>
        kind is PayloadKind.Video or PayloadKind.DiskImage or PayloadKind.Archive or PayloadKind.Audio;

    public static string Label(PayloadKind kind) =>
        kind switch
        {
            PayloadKind.Video => "Video",
            PayloadKind.Audio => "Audio",
            PayloadKind.Image => "Images",
            PayloadKind.Archive => "Archives",
            PayloadKind.DiskImage => "Disk images",
            _ => "Other"
        };

    public static PayloadKind? FromMagic(string path)
    {
        Span<byte> head = stackalloc byte[16];
        int read;
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                16,
                FileOptions.SequentialScan);
            read = stream.Read(head);
        }
        catch
        {
            return null;
        }

        if (read < 4)
        {
            return null;
        }

        if (head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3)
        {
            return PayloadKind.Video;
        }

        if (read >= 8 && head[4] == (byte)'f' && head[5] == (byte)'t' && head[6] == (byte)'y' && head[7] == (byte)'p')
        {
            if (read >= 12 && IsHeifBrand(head[8], head[9], head[10], head[11]))
            {
                return PayloadKind.Image;
            }

            return PayloadKind.Video;
        }

        if (head[0] == (byte)'R' && head[1] == (byte)'I' && head[2] == (byte)'F' && head[3] == (byte)'F' && read >= 12)
        {
            if (head[8] == (byte)'A' && head[9] == (byte)'V' && head[10] == (byte)'I')
            {
                return PayloadKind.Video;
            }

            if (head[8] == (byte)'W' && head[9] == (byte)'A' && head[10] == (byte)'V' && head[11] == (byte)'E')
            {
                return PayloadKind.Audio;
            }

            if (head[8] == (byte)'W' && head[9] == (byte)'E' && head[10] == (byte)'B' && head[11] == (byte)'P')
            {
                return PayloadKind.Image;
            }
        }

        if (head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
        {
            return PayloadKind.Image;
        }

        if (head[0] == 0x89 && head[1] == (byte)'P' && head[2] == (byte)'N' && head[3] == (byte)'G')
        {
            return PayloadKind.Image;
        }

        if (head[0] == (byte)'G' && head[1] == (byte)'I' && head[2] == (byte)'F' && head[3] == (byte)'8')
        {
            return PayloadKind.Image;
        }

        if (head[0] == (byte)'I' && head[1] == (byte)'D' && head[2] == (byte)'3')
        {
            return PayloadKind.Audio;
        }

        if (head[0] == (byte)'f' && head[1] == (byte)'L' && head[2] == (byte)'a' && head[3] == (byte)'C')
        {
            return PayloadKind.Audio;
        }

        if (head[0] == (byte)'O' && head[1] == (byte)'g' && head[2] == (byte)'g' && head[3] == (byte)'S')
        {
            return PayloadKind.Audio;
        }

        if (head[0] == (byte)'P' && head[1] == (byte)'K' && (head[2] == 3 || head[2] == 5 || head[2] == 7))
        {
            return PayloadKind.Archive;
        }

        if (head[0] == (byte)'R' && head[1] == (byte)'a' && head[2] == (byte)'r' && head[3] == (byte)'!')
        {
            return PayloadKind.Archive;
        }

        if (head[0] == (byte)'7' && head[1] == (byte)'z' && head[2] == 0xBC && head[3] == 0xAF)
        {
            return PayloadKind.Archive;
        }

        if (head[0] == 0x1F && head[1] == 0x8B)
        {
            return PayloadKind.Archive;
        }

        if (read >= 8 &&
            head[0] == (byte)'v' && head[1] == (byte)'h' && head[2] == (byte)'d' && head[3] == (byte)'x' &&
            head[4] == (byte)'f' && head[5] == (byte)'i' && head[6] == (byte)'l' && head[7] == (byte)'e')
        {
            return PayloadKind.DiskImage;
        }

        return null;
    }

    private static bool IsHeifBrand(byte a, byte b, byte c, byte d) =>
        a == (byte)'h' && (b == (byte)'e' || b == (byte)'i') && (c == (byte)'i' || c == (byte)'c' || c == (byte)'s');
}

/// <summary>
/// Limits magic-byte opens during enumerate (first few unknown files ≥ 1 MB).
/// </summary>
public sealed class MagicPeekBudget
{
    public const int MaxUnknownPeeks = 8;
    public const int MaxPackSkipPeeks = 64_000;
    public const long MinPeekBytes = 1024 * 1024;

    private int _unknown;
    private readonly int _max;
    private readonly long _minBytes;

    public MagicPeekBudget(int maxUnknownPeeks = MaxUnknownPeeks, long minPeekBytes = MinPeekBytes)
    {
        _max = maxUnknownPeeks > 0 ? maxUnknownPeeks : MaxUnknownPeeks;
        _minBytes = minPeekBytes;
    }

    public static MagicPeekBudget ForPackSkip() => new(MaxPackSkipPeeks, 0);

    public bool TryConsumeUnknown(long size)
    {
        if (size < _minBytes || _unknown >= _max)
        {
            return false;
        }

        _unknown++;
        return true;
    }
}

public sealed class PayloadInventory
{
    private readonly int[] _files = new int[6];
    private readonly long[] _bytes = new long[6];

    public int TotalFiles { get; private set; }
    public long TotalBytes { get; private set; }
    public long LargestFileBytes { get; private set; }

    public int Files(PayloadKind kind) => _files[(int)kind];
    public long Bytes(PayloadKind kind) => _bytes[(int)kind];

    public void Add(PayloadKind kind, long size)
    {
        var i = (int)kind;
        if (i < 0 || i >= _files.Length)
        {
            i = 0;
        }

        _files[i]++;
        _bytes[i] += size;
        TotalFiles++;
        TotalBytes += size;
        if (size > LargestFileBytes)
        {
            LargestFileBytes = size;
        }
    }

    public static PayloadInventory FromFiles(IEnumerable<FileRecord> files, MagicPeekBudget? peek = null)
    {
        var inventory = new PayloadInventory();
        foreach (var file in files)
        {
            inventory.Add(FileClassifier.Classify(file.SourcePath, file.Size, peek), file.Size);
        }

        return inventory;
    }

    public long SequentialBytes =>
        Bytes(PayloadKind.Video) + Bytes(PayloadKind.Audio) +
        Bytes(PayloadKind.Archive) + Bytes(PayloadKind.DiskImage);

    public bool SequentialDominates =>
        TotalBytes > 0 && SequentialBytes >= TotalBytes / 2;

    public bool ShouldProbeUnbuffered(UnbufferedIoPolicy policy) =>
        LargestFileBytes >= policy.LargeFileBytes || SequentialDominates;

    public string FormatSummary()
    {
        var parts = new List<string>();
        foreach (var kind in new[]
                 {
                     PayloadKind.Video, PayloadKind.Audio, PayloadKind.Image,
                     PayloadKind.Archive, PayloadKind.DiskImage, PayloadKind.Other
                 })
        {
            if (_files[(int)kind] == 0)
            {
                continue;
            }

            parts.Add(
                $"{FileClassifier.Label(kind)}: {_files[(int)kind]} files, {ByteFormatter.ToString(_bytes[(int)kind])}");
        }

        return string.Join("; ", parts);
    }
}
