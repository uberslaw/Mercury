using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Mercury;

public partial class ThemePanel
{
    public ThemePanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) =>
        ThemeChrome.EditorJumpRequested += OnEditorJump;

    private void OnUnloaded(object sender, RoutedEventArgs e) =>
        ThemeChrome.EditorJumpRequested -= OnEditorJump;

    private void OnEditorJump(IReadOnlyList<string> keys, bool pin)
    {
        if (DataContext is not ThemeViewModel vm)
        {
            return;
        }

        vm.HighlightKeys(keys);
        if (!pin || keys.Count == 0)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            foreach (var fe in FindHighlighted(this))
            {
                fe.BringIntoView();
            }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private static IEnumerable<FrameworkElement> FindHighlighted(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { DataContext: ThemeColorItem { IsHighlighted: true } } colorRow)
            {
                yield return colorRow;
            }
            else if (child is FrameworkElement { DataContext: ThemeFontItem { IsHighlighted: true } } fontRow)
            {
                yield return fontRow;
            }

            foreach (var nested in FindHighlighted(child))
            {
                yield return nested;
            }
        }
    }

    private void ColorRow_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!ThemeChrome.HelpersEnabled)
        {
            return;
        }

        if (sender is FrameworkElement { DataContext: ThemeColorItem item })
        {
            ThemeChrome.HoverKeys([item.Key]);
        }
    }

    private void ColorRow_MouseLeave(object sender, MouseEventArgs e) =>
        ThemeChrome.ClearHover();

    private void ColorRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!ThemeChrome.HelpersEnabled)
        {
            return;
        }

        if (sender is FrameworkElement { DataContext: ThemeColorItem item })
        {
            ThemeChrome.Pin(item.Key);
        }
    }

    private void FontRow_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!ThemeChrome.HelpersEnabled)
        {
            return;
        }

        if (sender is FrameworkElement { DataContext: ThemeFontItem item })
        {
            ThemeChrome.HoverKeys([item.Key]);
        }
    }

    private void FontRow_MouseLeave(object sender, MouseEventArgs e) =>
        ThemeChrome.ClearHover();

    private void FontRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!ThemeChrome.HelpersEnabled)
        {
            return;
        }

        if (sender is FrameworkElement { DataContext: ThemeFontItem item })
        {
            ThemeChrome.Pin(item.Key);
        }
    }

    private void HexBox_TextChanged(object sender, TextChangedEventArgs e) =>
        QueueApplyHex(sender, normalize: false);

    private void HexBox_LostFocus(object sender, RoutedEventArgs e) =>
        QueueApplyHex(sender, normalize: true);

    private void HexBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return))
        {
            return;
        }

        QueueApplyHex(sender, normalize: true);
        e.Handled = true;
    }

    private void HexBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        QueueApplyHex(box, normalize: false);
    }

    private static void QueueApplyHex(object sender, bool normalize)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        try
        {
            box.Dispatcher.BeginInvoke(() => ApplyHex(box, normalize), System.Windows.Threading.DispatcherPriority.Input);
        }
        catch (Exception ex)
        {
            ThemeService.Log("QueueApplyHex failed", ex);
        }
    }

    private static void ApplyHex(object sender, bool normalize)
    {
        try
        {
            if (sender is TextBox { DataContext: ThemeColorItem item } box)
            {
                item.ApplyHex(box.Text, normalize);
            }
        }
        catch (Exception ex)
        {
            ThemeService.Log("ApplyHex failed", ex);
        }
    }
}
