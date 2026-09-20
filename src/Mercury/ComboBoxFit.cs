using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace Mercury;

public static class ComboBoxFit
{
    public static readonly DependencyProperty ToContentProperty = DependencyProperty.RegisterAttached(
        "ToContent",
        typeof(bool),
        typeof(ComboBoxFit),
        new PropertyMetadata(false, OnToContentChanged));

    public static readonly DependencyProperty PreserveTextProperty = DependencyProperty.RegisterAttached(
        "PreserveText",
        typeof(bool),
        typeof(ComboBoxFit),
        new PropertyMetadata(false, OnPreserveTextChanged));

    private static readonly DependencyProperty PreserveHookedProperty = DependencyProperty.RegisterAttached(
        "PreserveHooked",
        typeof(bool),
        typeof(ComboBoxFit));

    public static void SetToContent(DependencyObject element, bool value) =>
        element.SetValue(ToContentProperty, value);

    public static bool GetToContent(DependencyObject element) =>
        (bool)element.GetValue(ToContentProperty);

    public static void SetPreserveText(DependencyObject element, bool value) =>
        element.SetValue(PreserveTextProperty, value);

    public static bool GetPreserveText(DependencyObject element) =>
        (bool)element.GetValue(PreserveTextProperty);

    private static void OnToContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ComboBox combo)
        {
            return;
        }

        combo.Loaded -= OnFitNeed;
        combo.SelectionChanged -= OnFitNeed;
        if ((bool)e.NewValue)
        {
            combo.HorizontalAlignment = HorizontalAlignment.Left;
            combo.MinWidth = 0;
            combo.Loaded += OnFitNeed;
            combo.SelectionChanged += OnFitNeed;
            if (combo.IsLoaded)
            {
                FitToSelection(combo);
            }
        }
    }

    private static void OnFitNeed(object sender, RoutedEventArgs e) => FitToSelection((ComboBox)sender);

    internal static void FitToSelection(ComboBox combo)
    {
        var text = combo.SelectedItem switch
        {
            ComboBoxItem item => item.Content?.ToString() ?? "",
            string s => s,
            null => combo.Text ?? "",
            var other => other.ToString() ?? ""
        };
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(combo).PixelsPerDip;
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            combo.FlowDirection,
            new Typeface(combo.FontFamily, combo.FontStyle, combo.FontWeight, combo.FontStretch),
            combo.FontSize,
            Brushes.Black,
            dpi);

        var extra = combo.Padding.Left + combo.Padding.Right
            + combo.BorderThickness.Left + combo.BorderThickness.Right
            + ToggleWidth(combo)
            + 8;
        combo.Width = Math.Ceiling(formatted.WidthIncludingTrailingWhitespace + extra);
    }

    private static double ToggleWidth(ComboBox combo)
    {
        if (combo.IsLoaded)
        {
            var toggle = FindDescendant<ToggleButton>(combo);
            if (toggle is { ActualWidth: > 1 and < 40 })
            {
                return toggle.ActualWidth;
            }
        }

        return 22;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            var nested = FindDescendant<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static void OnPreserveTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ComboBox combo)
        {
            return;
        }

        combo.Loaded -= OnPreserveLoaded;
        if ((bool)e.NewValue)
        {
            combo.Loaded += OnPreserveLoaded;
            if (combo.IsLoaded)
            {
                HookPreserve(combo);
            }
        }
    }

    private static void OnPreserveLoaded(object sender, RoutedEventArgs e) =>
        HookPreserve((ComboBox)sender);

    private static void HookPreserve(ComboBox combo)
    {
        if ((bool)combo.GetValue(PreserveHookedProperty)
            || combo.ItemsSource is not INotifyCollectionChanged ncc)
        {
            return;
        }

        combo.SetValue(PreserveHookedProperty, true);
        ncc.CollectionChanged += (_, _) =>
        {
            if (!combo.IsLoaded)
            {
                return;
            }

            combo.Dispatcher.BeginInvoke(
                () => BindingOperations.GetBindingExpression(combo, ComboBox.TextProperty)?.UpdateTarget(),
                DispatcherPriority.DataBind);
        };
    }
}
