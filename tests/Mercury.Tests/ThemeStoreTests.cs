namespace Mercury.Tests;

public class ThemeStoreTests
{
    [Fact]
    public void ExportRoundtripsColorsAndFonts()
    {
        var path = Path.Combine(Path.GetTempPath(), "mercury-theme-" + Guid.NewGuid().ToString("N") + ThemeStore.FileExtension);
        try
        {
            var original = new ThemePalette
            {
                Name = "Lab",
                Format = ThemeStore.FileFormat,
                Colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["TextBrush"] = "#4a2f2c",
                    ["HeaderBrush"] = "#f4c2c1"
                },
                Fonts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Progress.Family"] = "Segoe UI",
                    ["Progress.Size"] = "15",
                    ["Console.Family"] = "Consolas",
                    ["Console.Size"] = "12"
                }
            };

            ThemeStore.ExportPalette(path, original);
            var json = File.ReadAllText(path);
            Assert.Contains("\"format\": \"mercury-theme\"", json, StringComparison.Ordinal);
            Assert.Contains("Progress.Family", json, StringComparison.Ordinal);
            Assert.Contains("TextBrush", json, StringComparison.Ordinal);

            var loaded = ThemeStore.ImportPalette(path);
            Assert.NotNull(loaded);
            Assert.Equal("Lab", loaded!.Name);
            Assert.Equal(ThemeStore.FileFormat, loaded.Format);
            Assert.Equal("#4a2f2c", loaded.Colors["TextBrush"]);
            Assert.Equal("Segoe UI", loaded.Fonts["Progress.Family"]);
            Assert.Equal("15", loaded.Fonts["Progress.Size"]);
            Assert.Equal("Consolas", loaded.Fonts["Console.Family"]);
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // temp leftover is OK
            }
        }
    }

    [Fact]
    public void ParsePaletteKeepsExactRgbHex()
    {
        const string json = """
            {
              "name": "lab",
              "format": "mercury-theme",
              "colors": { "HeaderBrush": "#b7b7ff" }
            }
            """;

        var palette = ThemeStore.ParsePalette(json);
        Assert.NotNull(palette);
        Assert.Equal("#b7b7ff", palette!.Colors["HeaderBrush"]);
    }

    [Fact]
    public void ParsePaletteAcceptsShippingDefaultShape()
    {
        const string json = """
            {
              "name": "default",
              "format": "mercury-theme",
              "colors": { "TextBrush": "#4a2f2c", "BgBrush": "#fff5e3" },
              "fonts": { "Progress.Family": "Segoe UI", "Progress.Size": "15" }
            }
            """;

        var palette = ThemeStore.ParsePalette(json);
        Assert.NotNull(palette);
        Assert.Equal("default", palette!.Name);
        Assert.Equal("#4a2f2c", palette.Colors["TextBrush"]);
        Assert.Equal("15", palette.Fonts["Progress.Size"]);
    }

    [Fact]
    public void SaveLoadKeepsWorkingSlotAndDoesNotRequireShippingDefault()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-theme-persist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            var catalog = new ThemeCatalog
            {
                ActiveThemeName = "Office",
                Working = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["TextBrush"] = "112233",
                    ["BgBrush"] = "#abcdef"
                },
                Themes =
                [
                    new ThemePalette
                    {
                        Name = "Office",
                        Colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["TextBrush"] = "#112233"
                        }
                    }
                ]
            };

            ThemeStore.Save(paths, catalog);
            Assert.True(File.Exists(paths.ThemesFile));

            var loaded = ThemeStore.Load(paths);
            Assert.True(ThemeStore.HasUserTheme(loaded));
            Assert.Equal("Office", loaded.ActiveThemeName);
            Assert.Equal("112233", loaded.Working["TextBrush"]);
            Assert.Equal("#abcdef", loaded.Working["BgBrush"]);
            Assert.Equal("#112233", loaded.Themes.Single(t => t.Name == "Office").Colors["TextBrush"]);
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
                // temp leftover is OK
            }
        }
    }
}
