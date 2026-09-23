using System.Windows;
using System.Windows.Controls;

namespace Mercury;

public partial class StatPairText : UserControl
{
    public static readonly DependencyProperty KeySizeGroupProperty =
        DependencyProperty.Register(
            nameof(KeySizeGroup),
            typeof(string),
            typeof(StatPairText),
            new PropertyMetadata("ProgressStatKey0", OnKeySizeGroupChanged));

    public StatPairText()
    {
        InitializeComponent();
        ApplyKeySizeGroup();
        DataContextChanged += (_, _) => ApplyKeySizeGroupFromPair();
    }

    public string KeySizeGroup
    {
        get => (string)GetValue(KeySizeGroupProperty);
        set => SetValue(KeySizeGroupProperty, value);
    }

    private static void OnKeySizeGroupChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is StatPairText pair)
        {
            pair.ApplyKeySizeGroup();
        }
    }

    private void ApplyKeySizeGroupFromPair()
    {
        if (DataContext is StatPair stats && ReadLocalValue(KeySizeGroupProperty) == DependencyProperty.UnsetValue)
        {
            KeySizeGroup = stats.KeySizeGroup;
        }

        ApplyKeySizeGroup();
    }

    private void ApplyKeySizeGroup()
    {
        if (KeyColumn is null)
        {
            return;
        }

        KeyColumn.SharedSizeGroup = string.IsNullOrWhiteSpace(KeySizeGroup) ? null : KeySizeGroup;
    }
}
