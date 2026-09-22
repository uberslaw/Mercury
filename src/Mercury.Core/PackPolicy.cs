namespace Mercury;

/// <summary>
/// Settings lists plus built-in compressed-type detection. Pack list wins; never-pack copies as-is.
/// Unspecified extensions keep automated classification.
/// </summary>
public static class PackPolicy
{
    public static string NormalizeExtension(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "";
        }

        var t = raw.Trim().Trim('"');
        if (t.StartsWith('*'))
        {
            t = t[1..];
        }

        t = t.Trim();
        if (t.Length == 0)
        {
            return "";
        }

        if (!t.StartsWith('.'))
        {
            t = "." + t;
        }

        if (t.Length == 1)
        {
            return "";
        }

        return t.ToLowerInvariant();
    }

    public static List<string> NormalizeList(IEnumerable<string>? items)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (items is null)
        {
            return list;
        }

        foreach (var item in items)
        {
            var ext = NormalizeExtension(item);
            if (ext.Length == 0 || !seen.Add(ext))
            {
                continue;
            }

            list.Add(ext);
        }

        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    public static HashSet<string> ToSet(IEnumerable<string>? items) =>
        new(NormalizeList(items), StringComparer.OrdinalIgnoreCase);

    public static List<string> DefaultNeverPackExtensions() =>
        FileClassifier.AllCompressedExtensions().ToList();

    public static bool IsForcedPack(string? path, JobOptions options)
    {
        var ext = ExtensionOf(path);
        return ext.Length > 0 && ToSet(options.PackExtensions).Contains(ext);
    }

    public static bool ShouldSkipPacking(FileRecord file, JobOptions options, MagicPeekBudget? peek = null)
    {
        if (IsForcedPack(file.SourcePath, options) || IsForcedPack(file.RelativePath, options))
        {
            return false;
        }

        var never = ToSet(options.NeverPackExtensions);
        var ext = ExtensionOf(file.SourcePath);
        if (ext.Length == 0)
        {
            ext = ExtensionOf(file.RelativePath);
        }

        if (never.Count > 0)
        {
            if (never.Contains(ext))
            {
                return true;
            }

            if (FileClassifier.IsCompressedExtension(ext))
            {
                return false;
            }
        }

        if (!options.SkipCompressedWhenPacking)
        {
            return false;
        }

        return FileClassifier.IsAlreadyCompressed(file, peek);
    }

    public static void ApplySettings(JobOptions options, BandwidthSettings settings)
    {
        options.PackExtensions = NormalizeList(settings.PackExtensions);
        options.NeverPackExtensions = settings.NeverPackExtensions is null
            ? DefaultNeverPackExtensions()
            : NormalizeList(settings.NeverPackExtensions);
    }

    private static string ExtensionOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        return NormalizeExtension(Path.GetExtension(path));
    }
}
