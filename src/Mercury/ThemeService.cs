using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Mercury;

public sealed record ThemeSlot(string Key, string Label, string DefaultHex, string Group);

public sealed record ThemeFontSlot(string Key, string Label, string DefaultFamily, double DefaultSize);

public sealed class ThemeService
{
    private readonly AppPaths _paths;
    private readonly Dictionary<string, SolidColorBrush> _brushes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Color> _pending = new(StringComparer.Ordinal);
    private readonly object _saveLock = new();
    private ThemeCatalog _catalog;
    private bool _applyQueued;
    private bool _persistDirty;
    private bool _reloadEditor;

    public ThemeService(AppPaths paths)
    {
        _paths = paths;
        _catalog = ThemeStore.Load(paths);
        AdoptWorkingSlot();
        InstallBrushes();
        var addedGrey = EnsureGreyTheme();
        var named = !string.IsNullOrWhiteSpace(_catalog.ActiveThemeName)
            ? _catalog.Themes.FirstOrDefault(t =>
                string.Equals(t.Name, _catalog.ActiveThemeName, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(t.Name, "working", StringComparison.OrdinalIgnoreCase))
            : null;
        var source = named is { Colors.Count: > 0 } ? named.Colors : WorkingOrCurrent();
        var fonts = named is { Fonts.Count: > 0 } ? named.Fonts : WorkingOrCurrentFonts();
        var missingWorking = _catalog.Working.Count == 0;
        var firstRun = !ThemeStore.HasUserTheme(_catalog) && source.Count == 0;
        if (firstRun)
        {
            ApplyPalette(DefaultColors(), persist: false);
            ApplyFonts(DefaultFonts(), persist: false);
            _catalog.PaletteVersion = Math.Max(4, _catalog.PaletteVersion);
            CaptureWorking();
            Persist();
        }
        else
        {
            ApplyPalette(MergeDefaults(source), persist: false);
            ApplyFonts(MergeFonts(fonts), persist: false);
            _catalog.PaletteVersion = Math.Max(4, _catalog.PaletteVersion);
            if (missingWorking)
            {
                CaptureWorking();
            }

            if (addedGrey || missingWorking)
            {
                Persist();
            }
        }
    }

    public static ThemeService? Instance { get; private set; }

    public static bool IsApplying { get; private set; }

    public static IReadOnlyList<ThemeSlot> Slots { get; } =
    [
        new("TextBrush", "Window / body text", "#4a2f2c", "Text"),
        new("MutedBrush", "Muted text", "#8c5e5a", "Text"),
        new("OnHeaderBrush", "Header text", "#4a2f2c", "Text"),
        new("StatusBarTextBrush", "Status bar text", "#4a2f2c", "Text"),
        new("TabSelectedForegroundBrush", "Selected tab text", "#4a2f2c", "Text"),
        new("TabUnselectedForegroundBrush", "Unselected tab text", "#8c5e5a", "Text"),
        new("ErrorLineBrush", "Console error text", "#7a3b3b", "Text"),
        new("ButtonForegroundBrush", "Toolbar button text", "#4a2f2c", "Buttons"),
        new("AccentForegroundBrush", "Start / Stop button text", "#4a2f2c", "Buttons"),
        new("ButtonBrush", "Toolbar button fill", "#ffdab8", "Buttons"),
        new("ButtonHoverBrush", "Button hover", "#ffdab8", "Buttons"),
        new("AccentBrush", "Accent / Start fill", "#f4c2c1", "Buttons"),
        new("CancelButtonBrush", "Cancel / Stop fill", "#fadadd", "Buttons"),
        new("BgBrush", "Window background", "#fff5e3", "Backgrounds"),
        new("PanelBrush", "Panel background", "#ffe7cb", "Backgrounds"),
        new("HeaderBrush", "Header background", "#f4c2c1", "Backgrounds"),
        new("StatusBarBrush", "Status bar background", "#f4c2c1", "Backgrounds"),
        new("TabBackgroundBrush", "Tab background", "#ffe7cb", "Backgrounds"),
        new("TabSelectedBackgroundBrush", "Selected tab background", "#fff5e3", "Backgrounds"),
        new("BorderBrush", "Border", "#fadadd", "Borders"),
        new("HeaderBarTrackBrush", "Header progress track", "#ffdab8", "Progress bars"),
        new("HeaderBarFillBrush", "Header progress fill", "#fff5e3", "Progress bars"),
        new("BarTrackBrush", "Job progress track", "#ffdab8", "Progress bars"),
        new("BarFillBrush", "Job progress fill", "#f4c2c1", "Progress bars"),
        new("OkBrush", "OK / completed", "#3f5c3a", "Status"),
        new("WarnBrush", "Warning", "#9a5348", "Status"),
        new("DangerBrush", "Danger / failed", "#7a3b3b", "Status"),
        new("InputBackgroundBrush", "Text box / combo background", "#ffe7cb", "Inputs / lists"),
        new("PanelAltBrush", "List / alternate panel", "#ffe7cb", "Inputs / lists"),
        new("ConsoleBackgroundBrush", "Console list background", "#ffe7cb", "Inputs / lists"),
        new("QueueBackgroundBrush", "Queue list background", "#ffe7cb", "Inputs / lists"),
        new("QueueStatusTransferBrush", "Queue: transferring", "#c5e4f7", "Queue status"),
        new("QueueStatusVerifyBrush", "Queue: verifying / rundown", "#ffe08a", "Queue status"),
        new("QueueStatusCompleteBrush", "Queue: complete", "#c5e8c8", "Queue status"),
        new("QueueStatusErrorBrush", "Queue: incomplete / error", "#f5b4b0", "Queue status"),
        new("QueueStatusPausedBrush", "Queue: paused", "#ffd0a8", "Queue status"),
        new("QueueStatusQueuedBrush", "Queue: pending / hold", "#d9d4d0", "Queue status")
    ];

    public static IReadOnlyList<ThemeFontSlot> FontSlots { get; } =
    [
        new("Progress", "Current / overall progress stats", "Segoe UI", 15),
        new("Label", "Labels / keys", "Segoe UI", 15),
        new("Value", "Values", "Segoe UI", 15),
        new("Body", "Body / tabs", "Segoe UI", 15),
        new("Button", "Buttons", "Segoe UI", 15),
        new("Console", "Console", "Consolas", 12)
    ];

    public bool ReloadEditor { get; private set; }

    public event Action? Changed;

    public string? ActiveThemeName => _catalog.ActiveThemeName;

    public IReadOnlyList<string> SavedThemeNames =>
        _catalog.Themes
            .Select(t => t.Name)
            .Where(n => !string.Equals(n, "working", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static void Initialize(AppPaths paths)
    {
        Instance ??= new ThemeService(paths);
    }

    internal static void InitializeForTests(AppPaths paths)
    {
        Instance = new ThemeService(paths);
    }

    public static ThemeService Current
    {
        get
        {
            if (Instance is null)
            {
                Initialize(new AppPaths());
            }

            return Instance!;
        }
    }

    public static void Log(string message, Exception? ex = null)
    {
        var line = ex is null ? $"Mercury.Theme: {message}" : $"Mercury.Theme: {message}: {ex}";
        Debug.WriteLine(line);
        Console.WriteLine(line);
    }

    public SolidColorBrush GetBrush(string key) =>
        _brushes.TryGetValue(key, out var brush) ? brush : new SolidColorBrush(Colors.Black);

    public Color GetColor(string key) => GetBrush(key).Color;

    public void SetColor(string key, Color color, bool persist = true)
    {
        try
        {
            color.A = 255;
            lock (_pending)
            {
                _pending[key] = color;
                if (persist)
                {
                    _persistDirty = true;
                }
            }

            ApplyNow();
        }
        catch (Exception ex)
        {
            Log("SetColor failed to queue", ex);
        }
    }

    public void ResetToDefaults()
    {
        _catalog.ActiveThemeName = null;
        QueuePalette(DefaultColors(), persist: true, reloadEditor: true);
        ApplyFonts(DefaultFonts(), persist: true);
    }

    public void LoadTheme(string name)
    {
        var theme = _catalog.Themes.FirstOrDefault(t =>
            string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        if (theme is null)
        {
            return;
        }

        _catalog.ActiveThemeName = theme.Name;
        QueuePalette(MergeDefaults(theme.Colors), persist: true, reloadEditor: true);
        ApplyFonts(MergeFonts(theme.Fonts), persist: true);
    }

    public bool SaveCurrentAs(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || string.Equals(name, "working", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var existing = _catalog.Themes.FirstOrDefault(t =>
            string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            existing = new ThemePalette { Name = name };
            _catalog.Themes.Add(existing);
        }

        existing.Name = name;
        existing.Colors = SnapshotCurrent();
        existing.Fonts = SnapshotFonts();
        _catalog.ActiveThemeName = name;
        CaptureWorking();
        Persist();
        NotifyChanged(reloadEditor: false);
        return true;
    }

    public bool SaveSelected(string name) => SaveCurrentAs(name);

    public void ExportCurrent(string path)
    {
        var palette = new ThemePalette
        {
            Name = string.IsNullOrWhiteSpace(System.IO.Path.GetFileNameWithoutExtension(path))
                ? "Mercury"
                : System.IO.Path.GetFileNameWithoutExtension(path)
                    .Replace(".mercury-theme", "", StringComparison.OrdinalIgnoreCase),
            Format = ThemeStore.FileFormat,
            Colors = SnapshotCurrent(),
            Fonts = SnapshotFonts()
        };
        ThemeStore.ExportPalette(path, palette);
    }

    public void ImportFile(string path)
    {
        var palette = ThemeStore.ImportPalette(path)
            ?? throw new InvalidDataException("Not a Mercury theme file (need colours and/or fonts).");
        _catalog.ActiveThemeName = null;
        QueuePalette(MergeDefaults(palette.Colors), persist: true, reloadEditor: true);
        ApplyFonts(MergeFonts(palette.Fonts), persist: true);
    }

    public void DeleteTheme(string name)
    {
        if (string.Equals(name, "working", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _catalog.Themes.RemoveAll(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        if (string.Equals(_catalog.ActiveThemeName, name, StringComparison.OrdinalIgnoreCase))
        {
            _catalog.ActiveThemeName = null;
        }

        Persist();
        NotifyChanged(reloadEditor: false);
    }

    public static bool TryParseHex(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim();
        if (s.StartsWith('#'))
        {
            s = s[1..];
        }

        if (s.Length == 3)
        {
            s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
        }

        if (s.Length == 8)
        {
            if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
            {
                return false;
            }

            color = Color.FromArgb(
                (byte)((argb >> 24) & 0xFF),
                (byte)((argb >> 16) & 0xFF),
                (byte)((argb >> 8) & 0xFF),
                (byte)(argb & 0xFF));
            return true;
        }

        if (s.Length != 6)
        {
            return false;
        }

        if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return false;
        }

        color = Color.FromRgb(
            (byte)((rgb >> 16) & 0xFF),
            (byte)((rgb >> 8) & 0xFF),
            (byte)(rgb & 0xFF));
        return true;
    }

    public static string ToHex(Color color) =>
        $"#{color.R:x2}{color.G:x2}{color.B:x2}";

    public static Color ContrastInvert(Color color)
    {
        var inverted = Color.FromRgb(
            (byte)(255 - color.R),
            (byte)(255 - color.G),
            (byte)(255 - color.B));
        var delta = Math.Abs(color.R - inverted.R)
                    + Math.Abs(color.G - inverted.G)
                    + Math.Abs(color.B - inverted.B);
        if (delta >= 180)
        {
            return inverted;
        }

        var luminance = (0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B);
        return luminance > 140 ? Colors.Black : Colors.White;
    }

    private void QueuePalette(IReadOnlyDictionary<string, string> colors, bool persist, bool reloadEditor = false)
    {
        if (reloadEditor)
        {
            _reloadEditor = true;
        }

        foreach (var slot in Slots)
        {
            var hex = colors.TryGetValue(slot.Key, out var stored) ? stored : slot.DefaultHex;
            if (!TryParseHex(hex, out var color))
            {
                TryParseHex(slot.DefaultHex, out color);
            }

            lock (_pending)
            {
                _pending[slot.Key] = color;
                if (persist)
                {
                    _persistDirty = true;
                }
            }
        }

        QueueFlush();
    }

    private void QueueFlush()
    {
        if (_applyQueued)
        {
            return;
        }

        _applyQueued = true;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            FlushPending();
            return;
        }

        dispatcher.BeginInvoke(FlushPending, DispatcherPriority.Input);
    }

    private void ApplyNow()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            FlushPending();
            return;
        }

        dispatcher.Invoke(FlushPending, DispatcherPriority.Send);
    }

    private void FlushPending()
    {
        _applyQueued = false;
        Dictionary<string, Color> batch;
        bool persist;
        lock (_pending)
        {
            batch = new Dictionary<string, Color>(_pending, StringComparer.Ordinal);
            _pending.Clear();
            persist = _persistDirty;
            _persistDirty = false;
        }

        foreach (var (key, color) in batch)
        {
            ApplyColorInPlace(key, color);
        }

        if (persist && batch.Count > 0)
        {
            PersistWorkingSlot();
        }

        if (batch.Count > 0)
        {
            NotifyChanged(reloadEditor: false);
        }
    }

    private void ApplyPalette(IReadOnlyDictionary<string, string> colors, bool persist)
    {
        foreach (var slot in Slots)
        {
            var hex = colors.TryGetValue(slot.Key, out var stored) ? stored : slot.DefaultHex;
            if (!TryParseHex(hex, out var color))
            {
                TryParseHex(slot.DefaultHex, out color);
            }

            ApplyColorInPlace(slot.Key, color);
        }

        if (persist)
        {
            CaptureWorking();
            Persist();
        }
    }

    private void ApplyColorInPlace(string key, Color color)
    {
        IsApplying = true;
        try
        {
            color.A = 255;
            PublishLiveBrush(key, color);
            var hex = ToHex(color);
            _catalog.Current[key] = hex;
            _catalog.Working[key] = hex;
        }
        catch (Exception ex)
        {
            Log($"ApplyColorInPlace({key}) failed", ex);
            try
            {
                var hex = ToHex(color);
                _catalog.Current[key] = hex;
                _catalog.Working[key] = hex;
            }
            catch
            {
                // keep going
            }
        }
        finally
        {
            IsApplying = false;
        }
    }

    /// <summary>
    /// Application.Resources freezes Freezables on insert. The old path cloned a frozen
    /// brush, put the clone back in the dictionary (which froze it again), then set
    /// Color — that throws, was swallowed, and left the live UI + swatch on the old colour
    /// while hex/catalog moved. Build the brush with the target colour first, then replace
    /// the key so DynamicResource re-evaluates.
    /// </summary>
    private void PublishLiveBrush(string key, Color color)
    {
        var live = new SolidColorBrush(color);
        _brushes[key] = live;
        var resources = Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        if (resources.Contains(key))
        {
            resources.Remove(key);
        }

        resources.Add(key, live);
    }

    private void InstallBrushes()
    {
        var resources = Application.Current.Resources;
        foreach (var slot in Slots)
        {
            TryParseHex(slot.DefaultHex, out var fallback);
            SolidColorBrush brush;
            if (resources[slot.Key] is SolidColorBrush existing)
            {
                brush = existing.IsFrozen ? existing.Clone() : existing;
                if (!ReferenceEquals(resources[slot.Key], brush))
                {
                    resources[slot.Key] = brush;
                }
            }
            else
            {
                brush = new SolidColorBrush(fallback);
                resources[slot.Key] = brush;
            }

            _brushes[slot.Key] = brush;
        }
    }

    private void AdoptWorkingSlot()
    {
        var named = _catalog.Themes.FirstOrDefault(t =>
            string.Equals(t.Name, "working", StringComparison.OrdinalIgnoreCase));
        if (_catalog.Working.Count == 0 && named is not null && named.Colors.Count > 0)
        {
            _catalog.Working = new Dictionary<string, string>(named.Colors, StringComparer.OrdinalIgnoreCase);
        }
    }

    private IReadOnlyDictionary<string, string> WorkingOrCurrent()
    {
        if (_catalog.Working.Count > 0)
        {
            return _catalog.Working;
        }

        return _catalog.Current;
    }

    private void CaptureWorking()
    {
        var snap = SnapshotCurrent();
        _catalog.Current = snap;
        _catalog.Working = new Dictionary<string, string>(snap, StringComparer.OrdinalIgnoreCase);
        var fonts = SnapshotFonts();
        _catalog.CurrentFonts = fonts;
        _catalog.WorkingFonts = new Dictionary<string, string>(fonts, StringComparer.OrdinalIgnoreCase);
        UpsertWorkingTheme(snap, fonts);
    }

    private void UpsertWorkingTheme(Dictionary<string, string> colors, Dictionary<string, string>? fonts = null)
    {
        var existing = _catalog.Themes.FirstOrDefault(t =>
            string.Equals(t.Name, "working", StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            existing = new ThemePalette { Name = "working" };
            _catalog.Themes.Add(existing);
        }

        existing.Name = "working";
        existing.Colors = new Dictionary<string, string>(colors, StringComparer.OrdinalIgnoreCase);
        existing.Fonts = new Dictionary<string, string>(fonts ?? SnapshotFonts(), StringComparer.OrdinalIgnoreCase);
    }

    private void PersistWorkingSlot()
    {
        var fonts = _catalog.WorkingFonts.Count > 0 ? _catalog.WorkingFonts : SnapshotFonts();
        _catalog.WorkingFonts = new Dictionary<string, string>(fonts, StringComparer.OrdinalIgnoreCase);
        _catalog.Current = new Dictionary<string, string>(_catalog.Working, StringComparer.OrdinalIgnoreCase);
        _catalog.CurrentFonts = new Dictionary<string, string>(_catalog.WorkingFonts, StringComparer.OrdinalIgnoreCase);
        UpsertWorkingTheme(_catalog.Working, _catalog.WorkingFonts);
        Persist();
    }

    private Dictionary<string, string> SnapshotCurrent()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in Slots)
        {
            if (_catalog.Working.TryGetValue(slot.Key, out var stored) && TryParseHex(stored, out var parsed))
            {
                map[slot.Key] = ToHex(parsed);
                continue;
            }

            map[slot.Key] = ToHex(GetColor(slot.Key));
        }

        return map;
    }

    public void SetFont(string key, string family, double size)
    {
        var slot = FontSlots.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));
        if (slot is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(family))
        {
            family = slot.DefaultFamily;
        }

        if (size < 8)
        {
            size = slot.DefaultSize;
        }

        ApplyFontSlot(slot, family, size);
        _catalog.CurrentFonts[slot.Key + ".Family"] = family;
        _catalog.CurrentFonts[slot.Key + ".Size"] = size.ToString("0.###", CultureInfo.InvariantCulture);
        CaptureWorking();
        Persist();
        NotifyChanged(reloadEditor: false);
    }

    public (string Family, double Size) GetFont(string key)
    {
        var slot = FontSlots.First(s => s.Key == key);
        var family = slot.DefaultFamily;
        var size = slot.DefaultSize;
        var fonts = WorkingOrCurrentFonts();
        if (fonts.TryGetValue(slot.Key + ".Family", out var storedFamily) && !string.IsNullOrWhiteSpace(storedFamily))
        {
            family = storedFamily;
        }

        if (fonts.TryGetValue(slot.Key + ".Size", out var storedSize) &&
            double.TryParse(storedSize, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            size = parsed;
        }

        return (family, size);
    }

    private void ApplyFonts(IReadOnlyDictionary<string, string> fonts, bool persist)
    {
        foreach (var slot in FontSlots)
        {
            var family = fonts.TryGetValue(slot.Key + ".Family", out var f) && !string.IsNullOrWhiteSpace(f)
                ? f
                : slot.DefaultFamily;
            var size = slot.DefaultSize;
            if (fonts.TryGetValue(slot.Key + ".Size", out var s) &&
                double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                size = parsed;
            }

            ApplyFontSlot(slot, family, size);
        }

        if (persist)
        {
            CaptureWorking();
            Persist();
        }
    }

    private static void ApplyFontSlot(ThemeFontSlot slot, string family, double size)
    {
        var resources = Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        resources[slot.Key + "FontFamily"] = new FontFamily(family);
        resources[slot.Key + "FontSize"] = size;
        if (slot.Key == "Body")
        {
            resources["OptionsFontSize"] = size;
            resources["ControlMinHeight"] = Math.Max(28, size * 2);
            var pad = Math.Max(4, size * 0.35);
            resources["ControlPadding"] = new Thickness(8, pad, 8, pad);
        }
    }

    private Dictionary<string, string> SnapshotFonts()
    {
        var map = DefaultFonts();
        foreach (var slot in FontSlots)
        {
            if (Application.Current?.Resources[slot.Key + "FontFamily"] is FontFamily family)
            {
                map[slot.Key + ".Family"] = family.Source;
            }

            if (Application.Current?.Resources[slot.Key + "FontSize"] is double sz)
            {
                map[slot.Key + ".Size"] = sz.ToString("0.###", CultureInfo.InvariantCulture);
            }
        }

        return map;
    }

    private IReadOnlyDictionary<string, string> WorkingOrCurrentFonts()
    {
        if (_catalog.WorkingFonts.Count > 0)
        {
            return _catalog.WorkingFonts;
        }

        return _catalog.CurrentFonts;
    }

    private static Dictionary<string, string> DefaultFonts()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in FontSlots)
        {
            map[slot.Key + ".Family"] = slot.DefaultFamily;
            map[slot.Key + ".Size"] = slot.DefaultSize.ToString("0.###", CultureInfo.InvariantCulture);
        }

        OverlayShipping(map, fonts: true);
        return map;
    }

    private static ThemePalette? _shippingDefault;
    private static bool _shippingTried;

    private static void OverlayShipping(Dictionary<string, string> map, bool fonts)
    {
        var shipping = TryLoadShippingDefault();
        if (shipping is null)
        {
            return;
        }

        var source = fonts ? shipping.Fonts : shipping.Colors;
        foreach (var (key, value) in source)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                map[key] = value;
            }
        }
    }

    private static ThemePalette? TryLoadShippingDefault()
    {
        if (_shippingTried)
        {
            return _shippingDefault;
        }

        _shippingTried = true;
        try
        {
            var asm = typeof(ThemeService).Assembly;
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("default.mercury-theme.json", StringComparison.OrdinalIgnoreCase));
            if (name is null)
            {
                return null;
            }

            using var stream = asm.GetManifestResourceStream(name);
            if (stream is null)
            {
                return null;
            }

            using var reader = new StreamReader(stream);
            _shippingDefault = ThemeStore.ParsePalette(reader.ReadToEnd());
        }
        catch (Exception ex)
        {
            Log("Could not load shipping default theme", ex);
            _shippingDefault = null;
        }

        return _shippingDefault;
    }

    private static Dictionary<string, string> MergeFonts(IReadOnlyDictionary<string, string> fonts)
    {
        var map = DefaultFonts();
        foreach (var (key, value) in fonts)
        {
            map[key] = value;
        }

        return map;
    }

    private static Dictionary<string, string> DefaultColors()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in Slots)
        {
            map[slot.Key] = slot.DefaultHex;
        }

        OverlayShipping(map, fonts: false);
        return map;
    }

    private static Dictionary<string, string> MergeDefaults(IReadOnlyDictionary<string, string> colors)
    {
        var map = DefaultColors();
        foreach (var (key, hex) in colors)
        {
            map[key] = hex;
        }

        return map;
    }

    private bool EnsureGreyTheme()
    {
        if (_catalog.Themes.Any(t => string.Equals(t.Name, "Grey", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        _catalog.Themes.Add(new ThemePalette
        {
            Name = "Grey",
            Colors = GreyColors()
        });
        return true;
    }

    private static Dictionary<string, string> GreyColors() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["BgBrush"] = "#e7e8ec",
        ["PanelBrush"] = "#e7e8ec",
        ["PanelAltBrush"] = "#b1adad",
        ["TextBrush"] = "#68696d",
        ["MutedBrush"] = "#8c8c94",
        ["BorderBrush"] = "#8c8c94",
        ["HeaderBrush"] = "#68696d",
        ["OnHeaderBrush"] = "#e7e8ec",
        ["HeaderBarTrackBrush"] = "#8c8c94",
        ["HeaderBarFillBrush"] = "#e7e8ec",
        ["StatusBarBrush"] = "#68696d",
        ["StatusBarTextBrush"] = "#e7e8ec",
        ["AccentForegroundBrush"] = "#e7e8ec",
        ["ButtonForegroundBrush"] = "#68696d",
        ["ButtonBrush"] = "#b1adad",
        ["ButtonHoverBrush"] = "#ada8a5",
        ["AccentBrush"] = "#68696d",
        ["CancelButtonBrush"] = "#8c8c94",
        ["BarTrackBrush"] = "#b1adad",
        ["BarFillBrush"] = "#68696d",
        ["InputBackgroundBrush"] = "#b1adad",
        ["TabBackgroundBrush"] = "#b1adad",
        ["TabSelectedBackgroundBrush"] = "#e7e8ec",
        ["TabSelectedForegroundBrush"] = "#68696d",
        ["TabUnselectedForegroundBrush"] = "#8c8c94",
        ["ConsoleBackgroundBrush"] = "#b1adad",
        ["ErrorLineBrush"] = "#68696d",
        ["OkBrush"] = "#e7e8ec",
        ["WarnBrush"] = "#b1adad",
        ["DangerBrush"] = "#ada8a5",
        ["QueueBackgroundBrush"] = "#b1adad",
        ["QueueStatusTransferBrush"] = "#c5d4e4",
        ["QueueStatusVerifyBrush"] = "#e8d98a",
        ["QueueStatusCompleteBrush"] = "#c5d8c8",
        ["QueueStatusErrorBrush"] = "#d4b0ae",
        ["QueueStatusPausedBrush"] = "#d4b896",
        ["QueueStatusQueuedBrush"] = "#9a9696"
    };

    private void NotifyChanged(bool reloadEditor)
    {
        if (reloadEditor)
        {
            _reloadEditor = true;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            RaiseChanged();
            return;
        }

        dispatcher.BeginInvoke(RaiseChanged, DispatcherPriority.Input);
    }

    private void RaiseChanged()
    {
        ReloadEditor = _reloadEditor;
        _reloadEditor = false;
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log("Theme Changed listeners failed", ex);
        }
        finally
        {
            ReloadEditor = false;
        }
    }

    private void Persist()
    {
        try
        {
            lock (_saveLock)
            {
                ThemeStore.Save(_paths, _catalog);
            }
        }
        catch (Exception ex)
        {
            Log("Persist failed", ex);
        }
    }
}
