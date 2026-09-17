using System.Windows;
using System.Windows.Controls;

namespace Mercury.Catcher;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new CatcherViewModel();
        Closed += async (_, _) =>
        {
            if (DataContext is CatcherViewModel vm)
            {
                await vm.DisposeAsync();
            }
        };
    }

    private void PassphraseBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is CatcherViewModel vm && sender is PasswordBox box)
        {
            vm.SetPassphrase(box.Password);
        }
    }
}
