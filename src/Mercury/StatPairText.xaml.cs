using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace Mercury;

public partial class StatPairText : UserControl
{
    public static readonly DependencyProperty KeySizeGroupProperty =
        DependencyProperty.Register(
            nameof(KeySizeGroup),
            typeof(string),
            typeof(StatPairText),
            new PropertyMetadata("ProgressStatKey0", OnKeySizeGroupChanged));

    private bool _expanded;
    private bool _ownsValueText;
    private bool _fitting;

    public StatPairText()
    {
        InitializeComponent();
        ApplyKeySizeGroup();
        DataContextChanged += (_, _) =>
        {
            ApplyKeySizeGroupFromPair();
            ApplyExpandableMode();
        };
        Loaded += (_, _) => RefreshExpandableDisplay();
        SizeChanged += (_, _) => RefreshExpandableDisplay();
        ValueHost.SizeChanged += (_, _) => RefreshExpandableDisplay();
    }

    public string KeySizeGroup
    {
        get => (string)GetValue(KeySizeGroupProperty);
        set => SetValue(KeySizeGroupProperty, value);
    }

    public bool IsTypesExpanded => _expanded;

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

    private bool IsExpandable => DataContext is StatPair { Expandable: true, HasValue: true };

    private void ApplyExpandableMode()
    {
        if (ValueText is null || ValueRun is null || MoreToggle is null)
        {
            return;
        }

        if (IsExpandable)
        {
            if (!_ownsValueText)
            {
                BindingOperations.ClearBinding(ValueRun, Run.TextProperty);
                _ownsValueText = true;
            }

            ValueText.TextTrimming = TextTrimming.None;
            RefreshExpandableDisplay();
            return;
        }

        _expanded = false;
        MoreToggle.Visibility = Visibility.Collapsed;
        MoreToggle.IsChecked = false;
        ValueText.TextWrapping = TextWrapping.NoWrap;
        ValueText.TextTrimming = TextTrimming.CharacterEllipsis;
        if (_ownsValueText)
        {
            ValueRun.SetBinding(Run.TextProperty, new Binding(nameof(StatPair.Value)) { Mode = BindingMode.OneWay });
            _ownsValueText = false;
        }
    }

    private void MoreToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!IsExpandable)
        {
            return;
        }

        _expanded = !_expanded;
        RefreshExpandableDisplay();
    }

    private void RefreshExpandableDisplay()
    {
        if (_fitting || !IsExpandable || ValueRun is null || ValueText is null || MoreToggle is null || ValueHost is null)
        {
            return;
        }

        _fitting = true;
        try
        {
            var pair = (StatPair)DataContext;
            var parts = TypeSummaryFormat.SplitParts(pair.Value);
            if (parts.Count == 0)
            {
                MoreToggle.Visibility = Visibility.Collapsed;
                ValueRun.Text = pair.Value;
                ValueText.TextWrapping = TextWrapping.NoWrap;
                return;
            }

            if (_expanded)
            {
                MoreToggle.Visibility = Visibility.Visible;
                MoreToggle.IsChecked = true;
                MoreLabel.Text = TypeSummaryFormat.LessLabel;
                MoreChevron.Data = Geometry.Parse("M 0,5 L 4,1 L 8,5");
                MoreToggle.ToolTip = "Hide extra types";
                ValueText.TextWrapping = TextWrapping.Wrap;
                ValueText.TextTrimming = TextTrimming.None;
                ValueRun.Text = TypeSummaryFormat.JoinParts(parts, expanded: true);
                return;
            }

            ValueText.TextWrapping = TextWrapping.NoWrap;
            ValueText.TextTrimming = TextTrimming.None;
            MoreToggle.IsChecked = false;
            MoreLabel.Text = TypeSummaryFormat.MoreLabel;
            MoreChevron.Data = Geometry.Parse("M 0,1 L 4,5 L 8,1");
            MoreToggle.ToolTip = "Show the rest of the type mix";

            var full = TypeSummaryFormat.JoinParts(parts, expanded: false);
            var hostWidth = ValueHost.ActualWidth;
            if (hostWidth <= 0)
            {
                ValueRun.Text = full;
                MoreToggle.Visibility = Visibility.Collapsed;
                return;
            }

            MoreToggle.Visibility = Visibility.Hidden;
            MoreToggle.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var toggleWidth = MoreToggle.DesiredSize.Width + MoreToggle.Margin.Left + MoreToggle.Margin.Right;
            if (MeasureText(full) <= hostWidth)
            {
                MoreToggle.Visibility = Visibility.Collapsed;
                ValueRun.Text = full;
                return;
            }

            MoreToggle.Visibility = Visibility.Visible;
            var available = Math.Max(0, hostWidth - toggleWidth);
            var fitted = TypeSummaryFormat.FitOneLine(parts, MeasureText, available);
            ValueRun.Text = fitted.Text;
        }
        finally
        {
            _fitting = false;
        }
    }

    private double MeasureText(string text)
    {
        if (string.IsNullOrEmpty(text) || ValueRun is null)
        {
            return 0;
        }

        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(ValueRun.FontFamily, ValueRun.FontStyle, ValueRun.FontWeight, ValueRun.FontStretch),
            ValueRun.FontSize,
            Brushes.Black,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return formatted.WidthIncludingTrailingWhitespace;
    }
}
