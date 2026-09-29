using System.Windows;

namespace Mercury;

public partial class SettingsWindow : Window
{
    public SettingsWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
