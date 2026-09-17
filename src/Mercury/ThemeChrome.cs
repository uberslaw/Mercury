using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Mercury;

public static class ThemeChrome
{
    public static readonly DependencyProperty KeysProperty = DependencyProperty.RegisterAttached(
        "Keys",
        typeof(string),
        typeof(ThemeChrome),
        new PropertyMetadata(null, OnKeysChanged));

    public static readonly DependencyProperty RoleProperty = DependencyProperty.RegisterAttached(
        "Role",
        typeof(string),
        typeof(ThemeChrome),
        new PropertyMetadata(null));

    private static readonly List<WeakReference<FrameworkElement>> Targets = [];
    private static readonly HashSet<string> Pinned = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Hover = new(StringComparer.Ordinal);

    public static event Action<IReadOnlyList<string>, bool>? EditorJumpRequested;

    /// <summary>
    /// Inverse-outline helpers only run while Theme tab or the theme preview window is in session.
    /// </summary>
    public static bool HelpersEnabled { get; private set; }

    public static bool IsPinned => Pinned.Count > 0;

    public static void SetHelpersEnabled(bool enabled)
    {
        HelpersEnabled = enabled;
        if (!enabled)
        {
            Unpin();
        }
    }

    public static void SetKeys(DependencyObject element, string? value) =>
        element.SetValue(KeysProperty, value);

    public static string? GetKeys(DependencyObject element) =>
        (string?)element.GetValue(KeysProperty);

    public static void SetRole(DependencyObject element, string? value) =>
        element.SetValue(RoleProperty, value);

    public static string? GetRole(DependencyObject element) =>
        (string?)element.GetValue(RoleProperty);

    public static bool IsEditor(DependencyObject element) =>
        string.Equals(GetRole(element), "Editor", StringComparison.OrdinalIgnoreCase);

    public static void Highlight(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            ClearHover();
            return;
        }

        HoverKeys([key]);
    }

    public static void Pin(string key) => PinKeys([key]);

    public static void PinKeys(IReadOnlyList<string> keys)
    {
        if (!HelpersEnabled)
        {
            return;
        }

        Pinned.Clear();
        foreach (var key in keys.Where(k => !string.IsNullOrWhiteSpace(k)))
        {
            Pinned.Add(key);
        }

        Hover.Clear();
        Paint();
        EditorJumpRequested?.Invoke(Pinned.ToArray(), true);
    }

    public static bool Unpin()
    {
        var hadPin = Pinned.Count > 0 || Hover.Count > 0;
        Pinned.Clear();
        Hover.Clear();
        Paint();
        EditorJumpRequested?.Invoke([], false);
        return hadPin;
    }

    public static void HoverKeys(IReadOnlyList<string> keys)
    {
        if (!HelpersEnabled)
        {
            return;
        }

        Hover.Clear();
        foreach (var key in keys.Where(k => !string.IsNullOrWhiteSpace(k)))
        {
            Hover.Add(key);
        }

        Paint();
        if (Pinned.Count == 0)
        {
            EditorJumpRequested?.Invoke(Hover.ToArray(), false);
        }
    }

    public static void ClearHover()
    {
        Hover.Clear();
        Paint();
        if (Pinned.Count == 0)
        {
            EditorJumpRequested?.Invoke([], false);
        }
    }

    public static void HighlightKeys(IReadOnlyList<string> keys, bool pinEditor)
    {
        if (pinEditor)
        {
            PinKeys(keys);
            return;
        }

        if (keys.Count == 0)
        {
            ClearHover();
            return;
        }

        HoverKeys(keys);
    }

    public static IReadOnlyList<string> KeysOnAncestors(DependencyObject? start)
    {
        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var d = start; d is not null; d = d is Visual ? VisualTreeHelper.GetParent(d) : null)
        {
            foreach (var key in SplitKeys(GetKeys(d)))
            {
                if (seen.Add(key))
                {
                    keys.Add(key);
                }
            }
        }

        return keys;
    }

    private static void Paint()
    {
        try
        {
            if ((Pinned.Count > 0 || Hover.Count > 0) && !HelpersEnabled)
            {
                return;
            }

            Brush? pinOutline = null;
            Brush? hoverOutline = null;
            if (Pinned.Count > 0)
            {
                pinOutline = OutlineBrush(Pinned.First(), strong: true);
            }

            if (Hover.Count > 0 && (Pinned.Count == 0 || !Hover.SetEquals(Pinned)))
            {
                hoverOutline = OutlineBrush(Hover.First(), strong: Pinned.Count == 0);
            }

            for (var i = Targets.Count - 1; i >= 0; i--)
            {
                if (!Targets[i].TryGetTarget(out var fe))
                {
                    Targets.RemoveAt(i);
                    continue;
                }

                var targetKeys = SplitKeys(GetKeys(fe));
                var pinMatch = Pinned.Count > 0 && targetKeys.Any(Pinned.Contains);
                var hoverMatch = Hover.Count > 0 && targetKeys.Any(Hover.Contains);
                try
                {
                    if (fe is Border border && !IsEditor(fe))
                    {
                        if (pinMatch)
                        {
                            border.BorderBrush = pinOutline;
                        }
                        else if (hoverMatch)
                        {
                            border.BorderBrush = hoverOutline ?? Brushes.Transparent;
                        }
                        else
                        {
                            border.BorderBrush = Brushes.Transparent;
                        }
                    }
                }
                catch
                {
                    // Skip targets that are disconnected or frozen.
                }
            }
        }
        catch
        {
            // Highlight must never take down the app.
        }
    }

    private static Brush OutlineBrush(string key, bool strong)
    {
        try
        {
            var color = OutlineColor(key);
            var inverted = ThemeService.ContrastInvert(color);
            if (!strong)
            {
                inverted.A = 110;
            }

            var brush = new SolidColorBrush(inverted);
            brush.Freeze();
            return brush;
        }
        catch
        {
            return strong ? Brushes.Black : Brushes.Gray;
        }
    }

    private static Color OutlineColor(string key)
    {
        try
        {
            if (ThemeService.Slots.Any(s => string.Equals(s.Key, key, StringComparison.Ordinal)))
            {
                return ThemeService.Current.GetColor(key);
            }
        }
        catch
        {
            // Fall through to body text.
        }

        try
        {
            return ThemeService.Current.GetColor("TextBrush");
        }
        catch
        {
            return Colors.Black;
        }
    }

    private static void OnKeysChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe)
        {
            return;
        }

        Targets.RemoveAll(r => !r.TryGetTarget(out _));
        fe.MouseEnter -= OnTargetEnter;
        fe.MouseLeave -= OnTargetLeave;
        fe.PreviewMouseLeftButtonDown -= OnTargetClick;
        fe.PreviewMouseRightButtonDown -= OnTargetRightClick;
        if (e.NewValue is string { Length: > 0 })
        {
            Targets.Add(new WeakReference<FrameworkElement>(fe));
            if (!IsEditor(fe))
            {
                fe.MouseEnter += OnTargetEnter;
                fe.MouseLeave += OnTargetLeave;
                fe.PreviewMouseLeftButtonDown += OnTargetClick;
                fe.PreviewMouseRightButtonDown += OnTargetRightClick;
            }
        }
    }

    private static void OnTargetEnter(object sender, MouseEventArgs e)
    {
        if (!HelpersEnabled || sender is not DependencyObject d || IsEditor(d))
        {
            return;
        }

        var keys = SplitKeys(GetKeys(d));
        if (keys.Count == 0)
        {
            return;
        }

        HoverKeys(keys);
    }

    private static void OnTargetLeave(object sender, MouseEventArgs e) =>
        ClearHover();

    private static void OnTargetClick(object sender, MouseButtonEventArgs e)
    {
        if (!HelpersEnabled || sender is not DependencyObject d || IsEditor(d))
        {
            return;
        }

        var keys = KeysOnAncestors(d);
        if (keys.Count == 0)
        {
            return;
        }

        PinKeys(keys);
    }

    private static void OnTargetRightClick(object sender, MouseButtonEventArgs e)
    {
        if (!HelpersEnabled || sender is not DependencyObject d || IsEditor(d))
        {
            return;
        }

        Unpin();
        e.Handled = true;
    }

    private static IReadOnlyList<string> SplitKeys(string? keys) =>
        string.IsNullOrWhiteSpace(keys)
            ? []
            : keys.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
