using System.Windows;
using System.Windows.Input;

namespace Mercury;

public partial class ThemePreviewWindow : Window
{
    public ThemePreviewWindow()
    {
        InitializeComponent();
        PreviewMouseRightButtonDown += OnPreviewRightClick;
        PreviewKeyDown += OnPreviewKeyDown;
        Closed += (_, _) => ThemeChrome.Unpin();
    }

    private static void OnPreviewRightClick(object sender, MouseButtonEventArgs e)
    {
        ThemeChrome.Unpin();
        e.Handled = true;
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        ThemeChrome.Unpin();
        e.Handled = true;
    }
}
