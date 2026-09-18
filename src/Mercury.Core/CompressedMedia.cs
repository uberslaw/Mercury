namespace Mercury;

/// <summary>
/// Files that are already compressed. Pack-as-zip stores them without wrapping in the transport zip.
/// </summary>
public static class CompressedMedia
{
    public static bool IsAlreadyCompressed(string? path) =>
        FileClassifier.IsAlreadyCompressed(path);
}
