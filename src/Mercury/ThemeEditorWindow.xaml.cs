using System.Windows;

namespace Mercury;

public partial class ThemeEditorWindow : Window
{
    public ThemeEditorWindow(ThemeViewModel theme)
    {
        InitializeComponent();
        DataContext = theme;
    }
}
