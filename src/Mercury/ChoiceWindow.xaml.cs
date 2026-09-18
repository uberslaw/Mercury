using System.Windows;

namespace Mercury;

public enum ChoiceResult
{
    Cancel = 0,
    Primary = 1,
    Secondary = 2
}

public partial class ChoiceWindow : Window
{
    public ChoiceResult Result { get; private set; } = ChoiceResult.Cancel;

    public ChoiceWindow()
    {
        InitializeComponent();
    }

    public static ChoiceResult Show(
        Window? owner,
        string title,
        string body,
        string primary,
        string? secondary = null,
        string cancel = "Cancel")
    {
        var w = new ChoiceWindow
        {
            Owner = owner,
            Title = title
        };
        w.BodyText.Text = body;
        w.PrimaryButton.Content = primary;
        w.CancelButton.Content = cancel;
        if (string.IsNullOrWhiteSpace(secondary))
        {
            w.SecondaryButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            w.SecondaryButton.Content = secondary;
        }

        w.ShowDialog();
        return w.Result;
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        Result = ChoiceResult.Primary;
        DialogResult = true;
    }

    private void Secondary_Click(object sender, RoutedEventArgs e)
    {
        Result = ChoiceResult.Secondary;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Result = ChoiceResult.Cancel;
        DialogResult = false;
    }
}
