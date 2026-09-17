namespace Mercury;

/// <summary>
/// Files that are already compressed. Pack-as-zip stores them without compression,
/// so wrapping them in the transport zip only costs pack/unpack time.
/// </summary>
public static class CompressedMedia
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Video
        ".mkv", ".mp4", ".m4v", ".mov", ".avi", ".webm", ".wmv", ".m2ts", ".mts", ".ts",
        ".mpg", ".mpeg", ".vob", ".flv", ".3gp", ".m2v", ".ogv",
        // Archives
        ".zip", ".7z", ".rar", ".gz", ".bz2", ".xz", ".tar", ".tgz", ".tbz", ".tbz2",
        ".cab", ".iso", ".zst", ".lz", ".lzma", ".cab",
        // Photos
        ".jpg", ".jpeg", ".jpe", ".png", ".gif", ".webp", ".heic", ".heif", ".jxl", ".avif",
        // Audio
        ".mp3", ".aac", ".m4a", ".ogg", ".opus", ".flac", ".wma", ".m4b"
    };

    public static bool IsAlreadyCompressed(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var ext = Path.GetExtension(path);
        return ext.Length > 0 && Extensions.Contains(ext);
    }
}
