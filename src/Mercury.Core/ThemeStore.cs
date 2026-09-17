using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mercury;

public sealed class ThemePalette
{
    public string Name { get; set; } = "";
    public string Format { get; set; } = ThemeStore.FileFormat;
    public Dictionary<string, string> Colors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Fonts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ThemeCatalog
{
    public string? ActiveThemeName { get; set; }
    public int PaletteVersion { get; set; }
    public Dictionary<string, string> Current { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Working { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> CurrentFonts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> WorkingFonts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ThemePalette> Themes { get; set; } = [];
}

public static class ThemeStore
{
    public const string FileFormat = "mercury-theme";
    public const string FileExtension = ".mercury-theme.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonKeepNamePolicy.Instance,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static ThemeCatalog Load(AppPaths paths)
    {
        ThemeCatalog? fallback = null;
        foreach (var file in paths.ThemeFileCandidates())
        {
            var catalog = TryRead(file);
            if (catalog is null)
            {
                continue;
            }

            if (HasUserTheme(catalog))
            {
                return catalog;
            }

            fallback ??= catalog;
        }

        return fallback ?? new ThemeCatalog();
    }

    public static void Save(AppPaths paths, ThemeCatalog catalog)
    {
        var json = JsonSerializer.Serialize(catalog, Json);
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        WriteFile(paths.ThemesFile, json, written);
        foreach (var extra in paths.ExistingAlternateThemeFiles())
        {
            WriteFile(extra, json, written);
        }
    }

    public static bool HasUserTheme(ThemeCatalog catalog)
    {
        if (catalog.Working.Count > 0 || catalog.Current.Count > 0
            || catalog.WorkingFonts.Count > 0 || catalog.CurrentFonts.Count > 0)
        {
            return true;
        }

        return catalog.Themes.Any(t =>
            !string.Equals(t.Name, "working", StringComparison.OrdinalIgnoreCase)
            && (t.Colors.Count > 0 || t.Fonts.Count > 0));
    }

    private static ThemeCatalog? TryRead(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            var catalog = JsonSerializer.Deserialize<ThemeCatalog>(File.ReadAllText(file), Json);
            if (catalog is null)
            {
                return null;
            }

            catalog.Current = new Dictionary<string, string>(
                catalog.Current ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase);
            catalog.Working = new Dictionary<string, string>(
                catalog.Working ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase);
            catalog.CurrentFonts = new Dictionary<string, string>(
                catalog.CurrentFonts ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase);
            catalog.WorkingFonts = new Dictionary<string, string>(
                catalog.WorkingFonts ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase);
            foreach (var theme in catalog.Themes)
            {
                theme.Colors = new Dictionary<string, string>(
                    theme.Colors ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
                theme.Fonts = new Dictionary<string, string>(
                    theme.Fonts ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
            }

            return catalog;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteFile(string file, string json, HashSet<string> written)
    {
        try
        {
            var full = Path.GetFullPath(file);
            if (!written.Add(full))
            {
                return;
            }

            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(full, json);
        }
        catch
        {
            // best-effort dual persist
        }
    }

    public static void ExportPalette(string path, ThemePalette palette)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (string.IsNullOrWhiteSpace(palette.Format))
        {
            palette.Format = FileFormat;
        }

        File.WriteAllText(path, JsonSerializer.Serialize(palette, Json));
    }

    public static ThemePalette? ImportPalette(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return ParsePalette(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    public static ThemePalette? ParsePalette(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var palette = JsonSerializer.Deserialize<ThemePalette>(json, Json);
            if (palette is null)
            {
                return null;
            }

            palette.Colors = new Dictionary<string, string>(
                palette.Colors ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase);
            palette.Fonts = new Dictionary<string, string>(
                palette.Fonts ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(palette.Format))
            {
                palette.Format = FileFormat;
            }

            if (palette.Colors.Count == 0 && palette.Fonts.Count == 0)
            {
                return null;
            }

            return palette;
        }
        catch
        {
            return null;
        }
    }
}

file sealed class JsonKeepNamePolicy : JsonNamingPolicy
{
    public static readonly JsonKeepNamePolicy Instance = new();

    public override string ConvertName(string name) => name;
}
