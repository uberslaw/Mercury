using System.Windows;

namespace Mercury;

public partial class HelpWindow : Window
{
    public HelpWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
