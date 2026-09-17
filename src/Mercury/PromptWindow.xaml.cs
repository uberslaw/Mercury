using System.Windows;

namespace Mercury;

public partial class PromptWindow : Window
{
    public PromptWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    public string Prompt
    {
        get => PromptText.Text;
        set => PromptText.Text = value;
    }

    public string Value
    {
        get => ValueBox.Text;
        set => ValueBox.Text = value;
    }

    public static string? Ask(Window owner, string title, string prompt, string initial = "")
    {
        var w = new PromptWindow
        {
            Owner = owner,
            Title = title,
            Prompt = prompt,
            Value = initial
        };
        return w.ShowDialog() == true ? w.Value.Trim() : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Value))
        {
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
