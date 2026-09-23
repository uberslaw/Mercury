using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WpfPoint = System.Windows.Point;

namespace Mercury.Wpf.Tests;

[Collection("WpfSta")]
public class ProgressStatsLayoutTests
{
    [Fact]
    public void ProgressStatsLine_KeysLeftAlign_ColonsShareX_PerColumn()
    {
        WpfSta.Run(() =>
        {
            WpfSta.EnsureApp();
            Window? window = null;
            try
            {
                var stats = ProgressStats.From(
                    new JobProgress
                    {
                        Status = JobStatus.Copying,
                        StageName = "Copying",
                        StageIndex = 2,
                        StageCount = 4,
                        CurrentFile = @"Warren Truss\compressed\warren.zip",
                        FilesCopied = 1541,
                        FilesTotal = 25443,
                        BytesCopied = 43L * 1024 * 1024 * 1024,
                        BytesTotal = 811L * 1024 * 1024 * 1024,
                        BytesPerSecond = 6 * 1024 * 1024,
                        StartedUtc = DateTimeOffset.UtcNow.AddHours(-3),
                        StageStartedUtc = DateTimeOffset.UtcNow.AddMinutes(-25),
                        TypeSummary = "Archives: 25443 files, 811 GB"
                    },
                    jobIndex: 2,
                    jobCount: 2,
                    overall: new JobProgress { FilesCopied = 2131, FilesTotal = 51456 });

                var line = new ProgressStatsLine
                {
                    DataContext = stats,
                    Width = 980
                };
                window = new Window
                {
                    Width = 1000,
                    Height = 220,
                    Content = line
                };
                window.Show();
                WpfSta.Flush();
                line.UpdateLayout();
                WpfSta.Flush();

                var pairs = FindVisualChildren<StatPairText>(line).ToArray();
                Assert.True(pairs.Length >= 6, "expected Current/Overall stat pairs");

                foreach (var pair in pairs)
                {
                    Assert.Equal(TextAlignment.Left, pair.KeyText.TextAlignment);
                    Assert.Equal(FontWeights.Bold, pair.KeyText.FontWeight);
                    Assert.Equal(":", pair.ColonText.Text);
                }

                foreach (var column in pairs.GroupBy(p => p.KeySizeGroup))
                {
                    var members = column.ToArray();
                    if (members.Length < 2)
                    {
                        continue;
                    }

                    var keyXs = members.Select(p => LeftX(p.KeyText, line)).ToArray();
                    var colonXs = members.Select(p => LeftX(p.ColonText, line)).ToArray();

                    Assert.True(keyXs.Max() - keyXs.Min() < 1.5, $"keys in {column.Key} must start on the same X");
                    Assert.True(colonXs.Max() - colonXs.Min() < 1.5, $"colons in {column.Key} must share an X");
                    Assert.True(colonXs.Min() > keyXs.Max(), $"colon must sit to the right of key words in {column.Key}");
                }

                var job = Pair(pairs, "Job");
                var files = Pair(pairs, "Files");
                var types = Pair(pairs, "Types");
                var thisStage = Pair(pairs, "This stage");
                var speed = Pair(pairs, "Speed");

                Assert.Equal("ProgressStatKey0", job.KeySizeGroup);
                Assert.Equal(job.KeySizeGroup, files.KeySizeGroup);
                Assert.Equal(job.KeySizeGroup, types.KeySizeGroup);
                Assert.Equal("ProgressStatKey3", thisStage.KeySizeGroup);
                Assert.Equal(thisStage.KeySizeGroup, speed.KeySizeGroup);

                // Short keys stay left with the long key; they must not be pushed right to meet the colon.
                Assert.True(Math.Abs(LeftX(job.KeyText, line) - LeftX(types.KeyText, line)) < 1.5);
                Assert.True(RightX(job.KeyText, line) + 2 < LeftX(job.ColonText, line));
                Assert.True(job.KeyText.ActualWidth > 0 && types.KeyText.ActualWidth > 0);
                Assert.True(job.KeyText.ActualWidth + 4 < types.KeyText.ActualWidth,
                    $"Job glyph width ({job.KeyText.ActualWidth}) should be narrower than Types ({types.KeyText.ActualWidth}); both start on the same X so Job cannot be right-aligned to the colon.");
                Assert.True(Math.Abs(LeftX(job.ColonText, line) - LeftX(types.ColonText, line)) < 1.5);
            }
            finally
            {
                try
                {
                    window?.Close();
                }
                catch
                {
                    // test cleanup
                }
            }
        });
    }

    [Fact]
    public void HeaderClocks_ElapsedAndEta_KeysLeftAlign_ColonsShareX()
    {
        WpfSta.Run(() =>
        {
            WpfSta.EnsureApp();
            Window? window = null;
            try
            {
                var elapsed = new StatPairText
                {
                    DataContext = new StatPair("Elapsed", "2h 59m 40s"),
                    KeySizeGroup = "HeaderClockKey"
                };
                var eta = new StatPairText
                {
                    DataContext = new StatPair("ETA", "36h 32m"),
                    KeySizeGroup = "HeaderClockKey"
                };
                var clocks = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
                Grid.SetIsSharedSizeScope(clocks, true);
                clocks.Children.Add(elapsed);
                clocks.Children.Add(eta);

                var host = new Grid { Width = 720 };
                host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(clocks, 1);
                host.Children.Add(clocks);

                window = new Window
                {
                    Width = 760,
                    Height = 140,
                    Content = host
                };
                window.Show();
                WpfSta.Flush();
                host.UpdateLayout();
                WpfSta.Flush();

                Assert.Equal("Elapsed", elapsed.KeyText.Text);
                Assert.Equal("ETA", eta.KeyText.Text);
                Assert.Equal("HeaderClockKey", elapsed.KeySizeGroup);
                Assert.Equal(elapsed.KeySizeGroup, eta.KeySizeGroup);
                Assert.Equal(TextAlignment.Left, elapsed.KeyText.TextAlignment);
                Assert.Equal(TextAlignment.Left, eta.KeyText.TextAlignment);

                var elapsedKeyX = LeftX(elapsed.KeyText, clocks);
                var etaKeyX = LeftX(eta.KeyText, clocks);
                var elapsedColonX = LeftX(elapsed.ColonText, clocks);
                var etaColonX = LeftX(eta.ColonText, clocks);

                Assert.True(Math.Abs(elapsedKeyX - etaKeyX) < 1.5, $"key words must start on the same X ({elapsedKeyX} vs {etaKeyX})");
                Assert.True(Math.Abs(elapsedColonX - etaColonX) < 1.5, $"colons must share an X ({elapsedColonX} vs {etaColonX})");
                Assert.True(elapsed.KeyText.ActualWidth > eta.KeyText.ActualWidth + 4, "Elapsed glyphs should be wider than ETA");
                Assert.True(RightX(eta.KeyText, clocks) + 2 < etaColonX, "ETA must not right-align to the colon; gap after the shorter word is OK");
                Assert.True(elapsedColonX > elapsedKeyX, "colon sits to the right of Elapsed");
            }
            finally
            {
                try
                {
                    window?.Close();
                }
                catch
                {
                    // test cleanup
                }
            }
        });
    }

    [Fact]
    public void HeaderProgressBars_AreFiftyPercentTaller_QueueBarUnchanged()
    {
        WpfSta.Run(() =>
        {
            WpfSta.EnsureApp();
            Window? window = null;
            try
            {
                var height = Assert.IsType<double>(
                    System.Windows.Application.Current.FindResource("HeaderProgressBarHeight"));
                Assert.Equal(22.5, height);
                Assert.Equal(15 * 1.5, height);

                var current = new ProgressBar
                {
                    Value = 62,
                    Maximum = 100,
                    Height = height,
                    MinHeight = height
                };
                var overall = new ProgressBar
                {
                    Value = 28,
                    Maximum = 100,
                    Height = height,
                    MinHeight = height
                };
                var queue = new ProgressBar
                {
                    Value = 40,
                    Maximum = 100,
                    Height = 14
                };
                var host = new StackPanel { Width = 400 };
                host.Children.Add(current);
                host.Children.Add(overall);
                host.Children.Add(queue);
                window = new Window
                {
                    Width = 440,
                    Height = 160,
                    Content = host
                };
                window.Show();
                WpfSta.Flush();
                host.UpdateLayout();
                WpfSta.Flush();

                Assert.Equal(22.5, current.ActualHeight, 1);
                Assert.Equal(22.5, overall.ActualHeight, 1);
                Assert.Equal(14, queue.ActualHeight, 1);
            }
            finally
            {
                try
                {
                    window?.Close();
                }
                catch
                {
                    // test cleanup
                }
            }
        });
    }

    private static StatPairText Pair(IEnumerable<StatPairText> pairs, string key) =>
        Assert.Single(pairs, p => p.KeyText.Text == key);

    private static double LeftX(FrameworkElement element, Visual ancestor) =>
        element.TransformToAncestor(ancestor).Transform(new WpfPoint(0, 0)).X;

    private static double RightX(FrameworkElement element, Visual ancestor) =>
        element.TransformToAncestor(ancestor).Transform(new WpfPoint(element.ActualWidth, 0)).X;

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
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
