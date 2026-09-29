using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Mercury;

public partial class ProgressStatsLine : UserControl
{
    public ProgressStatsLine()
    {
        InitializeComponent();
        LayoutUpdated += (_, _) => PlaceCopyModes();
    }

    private void PlaceCopyModes()
    {
        if (CopyModeChecks is null || StatsGrid is null || StatsGrid.ActualWidth <= 0)
        {
            return;
        }

        CopyModeChecks.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var speed = FindSpeedPair();
        double x = 0;
        double y = 2;
        if (speed is not null && speed.ActualWidth > 1)
        {
            var origin = speed.TranslatePoint(new Point(0, 0), StatsHost);
            x = origin.X + speed.ActualWidth + 16;
            y = origin.Y;
            var available = StatsHost.ActualWidth - x;
            if (available < CopyModeChecks.DesiredSize.Width)
            {
                x = Math.Max(0, StatsHost.ActualWidth - CopyModeChecks.DesiredSize.Width);
                y = origin.Y;
            }
        }

        var margin = CopyModeChecks.Margin;
        if (Math.Abs(margin.Left - x) < 0.5 && Math.Abs(margin.Top - y) < 0.5)
        {
            return;
        }

        CopyModeChecks.Margin = new Thickness(x, y, 0, 0);
    }

    private StatPairText? FindSpeedPair()
    {
        foreach (var pair in FindVisualChildren<StatPairText>(StatsGrid))
        {
            if (pair.DataContext is StatPair stat && stat.Key == "Speed" && stat.HasValue)
            {
                return pair;
            }
        }

        return null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in FindVisualChildren<T>(child))
            {
                yield return nested;
            }
        }
    }
}
