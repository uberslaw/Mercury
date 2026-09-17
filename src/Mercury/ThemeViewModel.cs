using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WinForms = System.Windows.Forms;

namespace Mercury;

public sealed class ThemeColorGroup
{
    public ThemeColorGroup(string title, IReadOnlyList<ThemeColorItem> items)
    {
        Title = title;
        Items = items;
    }

    public string Title { get; }
    public IReadOnlyList<ThemeColorItem> Items { get; }
}

public sealed class ThemeColorItem : INotifyPropertyChanged
{
    private readonly ThemeService _service;
    private readonly SolidColorBrush _swatchBrush;
    private string _hex;
    private string _hint = "";
    private bool _suppressHex;
    private bool _highlighted;
    private Color _swatchColor;

    public ThemeColorItem(ThemeService service, ThemeSlot slot)
    {
        _service = service;
        Key = slot.Key;
        Label = slot.Label;
        _swatchColor = _service.GetColor(Key);
        _hex = ThemeService.ToHex(_swatchColor);
        _swatchBrush = new SolidColorBrush(_swatchColor);
        PickCommand = new RelayCommand(Pick);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Key { get; }
    public string Label { get; }

    /// <summary>
    /// Dedicated unfrozen swatch brush — never Application.Resources (binding that
    /// instance can freeze it) and never a new frozen brush per get (TemplateBinding
    /// then keeps the old fill). Mutate <see cref="SolidColorBrush.Color"/> in place.
    /// </summary>
    public Color SwatchColor
    {
        get => _swatchColor;
        private set
        {
            var color = Opaque(value);
            if (_swatchColor == color && _swatchBrush.Color == color)
            {
                return;
            }

            _swatchColor = color;
            if (_swatchBrush.Color != color)
            {
                _swatchBrush.Color = color;
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SwatchColor)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SwatchBrush)));
        }
    }

    public Brush SwatchBrush => _swatchBrush;

    public ICommand PickCommand { get; }

    public string Hex
    {
        get => _hex;
        set
        {
            if (_hex == value)
            {
                return;
            }

            _hex = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Hex)));
            if (_suppressHex)
            {
                return;
            }

            if (ThemeService.TryParseHex(value, out var color))
            {
                Hint = "";
                SwatchColor = Opaque(color);
                _service.SetColor(Key, SwatchColor);
                return;
            }

            Hint = string.IsNullOrWhiteSpace(value) ? "Enter a hex color" : "Use #RRGGBB or RRGGBB";
        }
    }

    public void ApplyHex(string? text, bool normalize)
    {
        if (ThemeService.TryParseHex(text, out var color))
        {
            Hint = "";
            SwatchColor = Opaque(color);
            _service.SetColor(Key, SwatchColor);
            if (normalize)
            {
                _suppressHex = true;
                Hex = ThemeService.ToHex(color);
                _suppressHex = false;
            }
            else if (!string.Equals(_hex, text, StringComparison.Ordinal))
            {
                _hex = text ?? "";
            }

            return;
        }

        Hint = string.IsNullOrWhiteSpace(text) ? "Enter a hex color" : "Use #RRGGBB or RRGGBB";
    }

    public string Hint
    {
        get => _hint;
        private set
        {
            if (_hint == value)
            {
                return;
            }

            _hint = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Hint)));
        }
    }

    public void RefreshFromBrush(bool overwriteHex = false)
    {
        SwatchColor = Opaque(_service.GetColor(Key));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HighlightBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SwatchBrush)));
        if (!overwriteHex)
        {
            return;
        }

        var hex = ThemeService.ToHex(_swatchColor);
        if (_hex == hex && Hint.Length == 0)
        {
            return;
        }

        _suppressHex = true;
        Hex = hex;
        Hint = "";
        _suppressHex = false;
    }

    public bool IsHighlighted
    {
        get => _highlighted;
        set
        {
            if (_highlighted == value)
            {
                return;
            }

            _highlighted = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHighlighted)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HighlightBrush)));
        }
    }

    public Brush HighlightBrush =>
        IsHighlighted
            ? new SolidColorBrush(Color.FromArgb(60, 80, 140, 220))
            : Brushes.Transparent;

    private void Pick()
    {
        var current = _swatchColor;
        using var dlg = new WinForms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true,
            SolidColorOnly = true,
            Color = System.Drawing.Color.FromArgb(255, current.R, current.G, current.B)
        };

        if (dlg.ShowDialog() != WinForms.DialogResult.OK)
        {
            return;
        }

        // Use the RGB the dialog confirmed (Color.R/G/B). Do not convert via HLS —
        // leftover Lum in the dialog is what the user OK'd, including pastels.
        var picked = dlg.Color;
        var color = Color.FromRgb(picked.R, picked.G, picked.B);
        ApplyHex(ThemeService.ToHex(color), normalize: true);
    }

    private static Color Opaque(Color color)
    {
        color.A = 255;
        return color;
    }
}

public sealed class ThemeViewModel : INotifyPropertyChanged
{
    private readonly ThemeService _service;
    private string? _selectedSavedTheme;
    private string _statusHint = "Changes apply immediately. Save writes the selected theme. Save As exports a file.";
    private bool _suppressSelection;

    public ThemeViewModel()
    {
        _service = ThemeService.Current;
        Groups = ThemeService.Slots
            .GroupBy(s => s.Group)
            .Select(g => new ThemeColorGroup(g.Key, g.Select(slot => new ThemeColorItem(_service, slot)).ToArray()))
            .ToArray();
        AllItems = Groups.SelectMany(g => g.Items).ToArray();
        FontItems = ThemeService.FontSlots.Select(slot => new ThemeFontItem(_service, slot)).ToArray();
        SavedThemes = new ObservableCollection<string>(_service.SavedThemeNames);
        _selectedSavedTheme = _service.ActiveThemeName;
        _service.Changed += OnThemeChanged;

        SaveCommand = new RelayCommand(SaveSelected, () => !string.IsNullOrWhiteSpace(SelectedSavedTheme));
        SaveAsCommand = new RelayCommand(SaveAsFile);
        ExportCommand = new RelayCommand(ExportFile);
        ImportCommand = new RelayCommand(ImportFile);
        LoadSelectedCommand = new RelayCommand(LoadSelected, () => !string.IsNullOrWhiteSpace(SelectedSavedTheme));
        DeleteCommand = new RelayCommand(DeleteSelected, () => !string.IsNullOrWhiteSpace(SelectedSavedTheme));
        ResetCommand = new RelayCommand(Reset);
        OpenPreviewCommand = new RelayCommand(() => OpenPreviewRequested?.Invoke());
        PopOutCommand = new RelayCommand(() => PopOutEditorRequested?.Invoke());
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Action? OpenPreviewRequested { get; set; }
    public Action? PopOutEditorRequested { get; set; }

    public IReadOnlyList<ThemeColorGroup> Groups { get; }
    public IReadOnlyList<ThemeColorItem> AllItems { get; }
    public IReadOnlyList<ThemeFontItem> FontItems { get; }
    public ObservableCollection<string> SavedThemes { get; }

    public ICommand SaveCommand { get; }
    public ICommand SaveAsCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand LoadSelectedCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand OpenPreviewCommand { get; }
    public ICommand PopOutCommand { get; }

    public string? SelectedSavedTheme
    {
        get => _selectedSavedTheme;
        set
        {
            if (_selectedSavedTheme == value)
            {
                return;
            }

            _selectedSavedTheme = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedSavedTheme)));
            (SaveCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (LoadSelectedCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DeleteCommand as RelayCommand)?.RaiseCanExecuteChanged();
            if (!_suppressSelection && !string.IsNullOrWhiteSpace(value))
            {
                _service.LoadTheme(value);
                StatusHint = $"Loaded “{value}”.";
            }
        }
    }

    public string StatusHint
    {
        get => _statusHint;
        private set
        {
            _statusHint = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusHint)));
        }
    }

    private void SaveSelected()
    {
        if (string.IsNullOrWhiteSpace(SelectedSavedTheme))
        {
            StatusHint = "Select a theme in the list to Save, or use Save As to export a file.";
            return;
        }

        if (!_service.SaveCurrentAs(SelectedSavedTheme))
        {
            return;
        }

        StatusHint = $"Saved “{SelectedSavedTheme}”.";
    }

    private void SaveAsFile()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save theme as",
            Filter = "Mercury theme (*.mercury-theme.json)|*.mercury-theme.json|JSON (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = ".mercury-theme.json",
            AddExtension = true,
            FileName = string.IsNullOrWhiteSpace(SelectedSavedTheme) ? "MyTheme.mercury-theme.json" : $"{SelectedSavedTheme}.mercury-theme.json"
        };

        if (dlg.ShowDialog() != true)
        {
            return;
        }

        try
        {
            _service.ExportCurrent(dlg.FileName);
            StatusHint = $"Exported to {dlg.FileName}";
        }
        catch (Exception ex)
        {
            StatusHint = $"Could not export: {ex.Message}";
        }
    }

    private void ExportFile()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export theme",
            Filter = "Mercury theme (*.mercury-theme.json)|*.mercury-theme.json|JSON (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = ".mercury-theme.json",
            AddExtension = true,
            FileName = string.IsNullOrWhiteSpace(SelectedSavedTheme)
                ? "Mercury.mercury-theme.json"
                : $"{SelectedSavedTheme}.mercury-theme.json"
        };

        if (dlg.ShowDialog() != true)
        {
            return;
        }

        try
        {
            _service.ExportCurrent(dlg.FileName);
            StatusHint = $"Exported {dlg.FileName} (colours and fonts). Attach this file in chat to make it the shipping default.";
        }
        catch (Exception ex)
        {
            StatusHint = $"Could not export: {ex.Message}";
        }
    }

    private void ImportFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import theme",
            Filter = "Mercury theme (*.mercury-theme.json)|*.mercury-theme.json|JSON (*.json)|*.json|All files (*.*)|*.*"
        };

        if (dlg.ShowDialog() != true)
        {
            return;
        }

        try
        {
            _service.ImportFile(dlg.FileName);
            _suppressSelection = true;
            SelectedSavedTheme = null;
            _suppressSelection = false;
            StatusHint = $"Imported {dlg.FileName}. Save if you want it in the named list.";
        }
        catch (Exception ex)
        {
            StatusHint = $"Could not import: {ex.Message}";
        }
    }

    private void LoadSelected()
    {
        if (string.IsNullOrWhiteSpace(SelectedSavedTheme))
        {
            return;
        }

        _service.LoadTheme(SelectedSavedTheme);
        StatusHint = $"Loaded “{SelectedSavedTheme}”.";
    }

    private void DeleteSelected()
    {
        var name = SelectedSavedTheme;
        var owner = Application.Current?.MainWindow;
        if (string.IsNullOrWhiteSpace(name) || owner is null)
        {
            return;
        }

        if (MessageBox.Show(owner, $"Delete saved theme “{name}”?", "Mercury",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        _service.DeleteTheme(name);
        RefreshSavedList(null);
        StatusHint = $"Deleted “{name}”.";
    }

    private void Reset()
    {
        _service.ResetToDefaults();
        _suppressSelection = true;
        SelectedSavedTheme = null;
        _suppressSelection = false;
        StatusHint = "Restored the default warm palette.";
    }

    private void OnThemeChanged()
    {
        var overwriteHex = _service.ReloadEditor;
        foreach (var item in AllItems)
        {
            item.RefreshFromBrush(overwriteHex);
        }

        foreach (var font in FontItems)
        {
            font.Refresh(overwrite: overwriteHex);
        }
    }

    public void HighlightKeys(IReadOnlyList<string> keys)
    {
        var set = keys.ToHashSet(StringComparer.Ordinal);
        foreach (var item in AllItems)
        {
            item.IsHighlighted = set.Contains(item.Key);
        }

        foreach (var font in FontItems)
        {
            font.IsHighlighted = set.Contains(font.Key);
        }
    }

    public void ClearRowHighlights()
    {
        foreach (var item in AllItems)
        {
            item.IsHighlighted = false;
        }

        foreach (var font in FontItems)
        {
            font.IsHighlighted = false;
        }
    }

    private void RefreshSavedList(string? select)
    {
        _suppressSelection = true;
        SavedThemes.Clear();
        foreach (var name in _service.SavedThemeNames)
        {
            SavedThemes.Add(name);
        }

        _selectedSavedTheme = select;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedSavedTheme)));
        (SaveCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (LoadSelectedCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DeleteCommand as RelayCommand)?.RaiseCanExecuteChanged();
        _suppressSelection = false;
    }
}

public sealed class ThemeFontItem : INotifyPropertyChanged
{
    private readonly ThemeService _service;
    private string _family;
    private string _sizeText;
    private bool _suppress;
    private bool _highlighted;

    public ThemeFontItem(ThemeService service, ThemeFontSlot slot)
    {
        _service = service;
        Key = slot.Key;
        Label = slot.Label;
        var current = service.GetFont(slot.Key);
        _family = current.Family;
        _sizeText = current.Size.ToString("0.###", CultureInfo.InvariantCulture);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Key { get; }
    public string Label { get; }

    public string Family
    {
        get => _family;
        set
        {
            if (_family == value)
            {
                return;
            }

            _family = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Family)));
            if (!_suppress)
            {
                Push();
            }
        }
    }

    public string SizeText
    {
        get => _sizeText;
        set
        {
            if (_sizeText == value)
            {
                return;
            }

            _sizeText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SizeText)));
            if (!_suppress)
            {
                Push();
            }
        }
    }

    public bool IsHighlighted
    {
        get => _highlighted;
        set
        {
            if (_highlighted == value)
            {
                return;
            }

            _highlighted = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHighlighted)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HighlightBrush)));
        }
    }

    public Brush HighlightBrush =>
        IsHighlighted
            ? new SolidColorBrush(Color.FromArgb(60, 80, 140, 220))
            : Brushes.Transparent;

    public void Refresh(bool overwrite = true)
    {
        if (!overwrite)
        {
            return;
        }

        var current = _service.GetFont(Key);
        _suppress = true;
        Family = current.Family;
        SizeText = current.Size.ToString("0.###", CultureInfo.InvariantCulture);
        _suppress = false;
    }

    private void Push()
    {
        if (!double.TryParse(_sizeText, NumberStyles.Float, CultureInfo.InvariantCulture, out var size))
        {
            return;
        }

        _service.SetFont(Key, _family, size);
    }
}
