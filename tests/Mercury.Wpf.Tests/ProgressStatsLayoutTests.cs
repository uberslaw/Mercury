using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    public void HeaderFileClocks_ThisFileAndFileEta_KeysLeftAlign_ColonsShareX()
    {
        WpfSta.Run(() =>
        {
            WpfSta.EnsureApp();
            Window? window = null;
            try
            {
                var thisFile = new StatPairText
                {
                    DataContext = new StatPair("This file", "1m 12s"),
                    KeySizeGroup = "FileClockKey"
                };
                var fileEta = new StatPairText
                {
                    DataContext = new StatPair("File ETA", "3m 40s"),
                    KeySizeGroup = "FileClockKey"
                };
                var clocks = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                Grid.SetIsSharedSizeScope(clocks, true);
                clocks.Children.Add(thisFile);
                clocks.Children.Add(fileEta);

                var host = new Grid { Width = 720 };
                host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
                host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
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

                Assert.Equal("This file", thisFile.KeyText.Text);
                Assert.Equal("File ETA", fileEta.KeyText.Text);
                Assert.Equal("FileClockKey", thisFile.KeySizeGroup);
                Assert.Equal(thisFile.KeySizeGroup, fileEta.KeySizeGroup);
                Assert.Equal(TextAlignment.Left, thisFile.KeyText.TextAlignment);
                Assert.Equal(TextAlignment.Left, fileEta.KeyText.TextAlignment);

                var thisKeyX = LeftX(thisFile.KeyText, clocks);
                var etaKeyX = LeftX(fileEta.KeyText, clocks);
                var thisColonX = LeftX(thisFile.ColonText, clocks);
                var etaColonX = LeftX(fileEta.ColonText, clocks);
                var thisY = TopY(thisFile, clocks);
                var etaY = TopY(fileEta, clocks);

                Assert.True(Math.Abs(thisY - etaY) < 2, $"This file and File ETA must share one line ({thisY} vs {etaY})");
                Assert.True(etaKeyX > RightX(thisFile, clocks), "File ETA sits to the right of This file");
                Assert.True(Math.Abs(thisFile.KeyColumn.ActualWidth - fileEta.KeyColumn.ActualWidth) < 1.5,
                    "shared FileClockKey keeps key columns the same width");
                Assert.True(RightX(thisFile.KeyText, clocks) + 2 < thisColonX, "This file must not right-align to the colon");
                Assert.True(RightX(fileEta.KeyText, clocks) + 2 < etaColonX, "File ETA must not right-align to the colon");
                Assert.True(thisKeyX < thisColonX);
                Assert.True(etaKeyX < etaColonX);
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

    [Fact]
    public void HeaderFileProgressBar_ShowsPathAndFileClocks()
    {
        WpfSta.Run(() =>
        {
            WpfSta.EnsureApp();
            var root = Path.Combine(Path.GetTempPath(), "mercury-filebar-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            MainWindow? window = null;
            try
            {
                var dest1 = Path.Combine(root, "d1");
                var dest2 = Path.Combine(root, "d2");
                Directory.CreateDirectory(dest1);
                Directory.CreateDirectory(dest2);
                var paths = new AppPaths(root);
                QueueStore.Save(paths,
                [
                    new Job { Name = "one", SourcePath = Path.Combine(root, "s1"), DestinationPath = dest1 },
                    new Job { Name = "two", SourcePath = Path.Combine(root, "s2"), DestinationPath = dest2 }
                ]);

                var now = DateTimeOffset.UtcNow;
                var vm = new MainViewModel(paths);
                vm.JobPercent = 40;
                vm.OverallPercent = 28;
                vm.CurrentFile = @"Warren Truss\clip.mkv";
                vm.StatusText = @"Copying Warren Truss\clip.mkv";
                vm.JobStats = ProgressStats.From(new JobProgress
                {
                    Status = JobStatus.Copying,
                    StageName = "Copying",
                    StageIndex = 3,
                    StageCount = 6,
                    CurrentFile = @"Warren Truss\clip.mkv",
                    CurrentFileBytesCopied = 42,
                    CurrentFileBytesTotal = 100,
                    CurrentFileStartedUtc = now.AddSeconds(-72),
                    FilesCopied = 3,
                    FilesTotal = 10,
                    BytesCopied = 42,
                    BytesTotal = 1000,
                    StartedUtc = now.AddMinutes(-5),
                    StageStartedUtc = now.AddMinutes(-2),
                    TypeSummary = "Video: 12 files, 2.93 TB (82%); Images: 126 files, 451 MB (<1%)"
                }, now);

                window = new MainWindow(vm)
                {
                    Width = 1080,
                    Height = 780,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -4000,
                    Top = 0
                };
                window.Show();
                WpfSta.Flush();
                window.UpdateLayout();
                WpfSta.Flush();

                Assert.True(vm.ShowOverallProgress);
                Assert.False(vm.ShowHeaderStatus);
                Assert.Equal(Visibility.Collapsed, window.HeaderStatusBanner.Visibility);
                Assert.DoesNotContain(
                    FindVisualChildren<TextBlock>(window.ProgressHeaderRoot)
                        .Where(t => t.IsVisible && t.ActualHeight > 0),
                    t => (t.Text ?? "").Contains("Copying Warren Truss", StringComparison.Ordinal));
                Assert.True(window.HeaderFileProgressBar.ActualHeight > 0);
                Assert.Equal(42, window.HeaderFileProgressBar.Value, 1);
                Assert.Contains("clip.mkv", window.HeaderFileProgressText.Text, StringComparison.Ordinal);
                Assert.Contains("42%", window.HeaderFileProgressText.Text, StringComparison.Ordinal);
                Assert.Null(window.HeaderFileProgressText.Effect);
                Assert.True(window.HeaderFileProgressHost.UseLayoutRounding);
                Assert.True(window.HeaderFileProgressHost.SnapsToDevicePixels);
                Assert.Equal(TextFormattingMode.Display, TextOptions.GetTextFormattingMode(window.HeaderFileProgressText));
                Assert.Equal("This file", window.HeaderThisFilePair.KeyText.Text);
                Assert.Equal("File ETA", window.HeaderFileEtaPair.KeyText.Text);
                Assert.Equal("1m 12s", vm.HeaderThisFile.Value);
                Assert.Equal(TextAlignment.Left, window.HeaderThisFilePair.KeyText.TextAlignment);
                Assert.Equal(window.HeaderThisFilePair.KeySizeGroup, window.HeaderFileEtaPair.KeySizeGroup);

                var thisKeyX = LeftX(window.HeaderThisFilePair.KeyText, window);
                var etaKeyX = LeftX(window.HeaderFileEtaPair.KeyText, window);
                var thisY = TopY(window.HeaderThisFilePair, window);
                var etaY = TopY(window.HeaderFileEtaPair, window);
                Assert.True(Math.Abs(thisY - etaY) < 3, $"This file and File ETA must share one line ({thisY} vs {etaY})");
                Assert.True(etaKeyX > RightX(window.HeaderThisFilePair, window), "File ETA sits to the right of This file");
                Assert.Equal(window.HeaderThisFilePair.KeySizeGroup, window.HeaderFileEtaPair.KeySizeGroup);
                Assert.True(thisKeyX < LeftX(window.HeaderThisFilePair.ColonText, window));

                var titleY = TopY(window.HeaderProgressTitle, window);
                var elapsedY = TopY(window.HeaderElapsedPair, window);
                var etaClockY = TopY(window.HeaderEtaPair, window);
                var chipY = TopY(window.HeaderProgressChip, window);
                Assert.True(Math.Abs(titleY - elapsedY) < 10, $"Progress and Elapsed must share a row ({titleY} vs {elapsedY})");
                Assert.True(Math.Abs(elapsedY - etaClockY) < 10, $"Elapsed and ETA must share a row ({elapsedY} vs {etaClockY})");
                Assert.True(Math.Abs(titleY - chipY) < 10, "the status chip sits on the Progress title row");
                Assert.True(RightX(window.HeaderProgressTitle, window) < LeftX(window.HeaderProgressChip, window));
                Assert.True(RightX(window.HeaderProgressChip, window) < LeftX(window.HeaderElapsedPair, window));
                Assert.True(RightX(window.HeaderElapsedPair, window) <= LeftX(window.HeaderEtaPair, window) + 1);

                var chipStyle = Assert.IsType<Style>(System.Windows.Application.Current.FindResource("QueueStatusChip"));
                var headerChips = FindVisualChildren<Border>(window.ProgressHeaderRoot)
                    .Where(b => Equals(b.Style, chipStyle) && b.IsVisible && b.ActualHeight > 0)
                    .ToArray();
                Assert.Single(headerChips);
                Assert.Same(window.HeaderProgressChip, headerChips[0]);

                var currentLeft = LeftX(window.HeaderCurrentProgressBar, window);
                var fileLeft = LeftX(window.HeaderFileProgressBar, window);
                var currentLabelLeft = LeftX(window.HeaderCurrentLabel, window);
                var clocksKeyLeft = LeftX(window.HeaderThisFilePair.KeyText, window);
                var fileRight = RightX(window.HeaderFileProgressBar, window);
                var overallLeft = LeftX(window.HeaderOverallProgressBar, window);
                Assert.True(Math.Abs(currentLeft - fileLeft) < 1.5, $"file bar left {fileLeft} must match Current {currentLeft}");
                Assert.True(Math.Abs(currentLeft - clocksKeyLeft) < 10, $"file clock keys left {clocksKeyLeft} must match the Current bar {currentLeft}");
                Assert.True(currentLabelLeft + 1 >= currentLeft, "Current label sits inside the Current bar, not on a row above");
                Assert.True(InsideVertically(window.HeaderCurrentLabel, window.HeaderCurrentProgressBar, window), "Current is an overlay on its bar");
                Assert.True(InsideVertically(window.HeaderOverallLabel, window.HeaderOverallProgressBar, window), "Overall is an overlay on its bar");
                Assert.True(clocksKeyLeft + 4 < overallLeft, "file clocks stay in the Current column, not under Overall");
                Assert.True(
                    Math.Abs(window.HeaderCurrentProgressBar.ActualWidth - window.HeaderFileProgressBar.ActualWidth) < 2,
                    $"file bar width {window.HeaderFileProgressBar.ActualWidth} must match Current {window.HeaderCurrentProgressBar.ActualWidth}");

                Assert.True(
                    fileRight + 4 < overallLeft,
                    $"file bar (right {fileRight}) must not span under Overall (left {overallLeft})");

                var currentBottom = BottomY(window.HeaderCurrentProgressBar, window);
                var clocksTop = TopY(window.HeaderThisFilePair, window);
                var clocksBottom = Math.Max(
                    BottomY(window.HeaderThisFilePair, window),
                    BottomY(window.HeaderFileEtaPair, window));
                var fileTop = TopY(window.HeaderFileProgressBar, window);
                Assert.True(clocksTop + 1 >= currentBottom, "This file / File ETA sit under the Current bar");
                Assert.True(clocksBottom <= fileTop + 1, "This file / File ETA sit just above the file bar");
                Assert.True(clocksTop + 1 >= BottomY(window.HeaderCurrentLabel, window), "Current is not a label row above the clocks");

                Assert.Equal(Visibility.Collapsed, window.HeaderStatusBanner.Visibility);
                Assert.Equal(Visibility.Collapsed, window.HeaderResultBanner.Visibility);
                Assert.Equal(Visibility.Collapsed, window.HeaderCloudBanner.Visibility);
                Assert.True(window.HeaderStatusBanner.ActualHeight < 1);
                Assert.True(window.HeaderResultBanner.ActualHeight < 1);
                Assert.True(window.HeaderCloudBanner.ActualHeight < 1);

                var typesBottom = BottomY(window.HeaderStatsLine.TypesPair, window);
                var startTop = TopY(window.HeaderRunButtons, window);
                var gap = startTop - typesBottom;
                Assert.InRange(gap, 4, 28);
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

                try
                {
                    Directory.Delete(root, true);
                }
                catch
                {
                    // leftover
                }
            }
        });
    }

    [Fact]
    public void GlobalMinMax_LabelsSitBesideBoxes()
    {
        WpfSta.Run(() =>
        {
            WpfSta.EnsureApp();
            var root = Path.Combine(Path.GetTempPath(), "mercury-global-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            MainWindow? window = null;
            try
            {
                var vm = new MainViewModel(new AppPaths(root));
                window = new MainWindow(vm)
                {
                    Width = 1080,
                    Height = 780,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -4000,
                    Top = 0
                };
                window.Show();
                WpfSta.Flush();
                window.UpdateLayout();
                WpfSta.Flush();

                var group = window.GlobalGroup;
                var minGap = LeftX(window.GlobalMinBox, group) - RightX(window.GlobalMinLabel, group);
                var maxGap = LeftX(window.GlobalMaxBox, group) - RightX(window.GlobalMaxLabel, group);
                var idleGap = LeftX(window.GlobalIdleBox, group) - RightX(window.GlobalIdleCheck, group);
                Assert.True(minGap >= -1 && minGap < 20, $"Min label must sit beside its box (gap {minGap})");
                Assert.True(maxGap >= -1 && maxGap < 20, $"Max label must sit beside its box (gap {maxGap})");
                Assert.True(idleGap >= -1 && idleGap < 20, $"Throttle Active PC must sit beside its box (gap {idleGap})");
                Assert.True(window.GlobalMinBox.ActualWidth >= 70);
                Assert.Equal(window.GlobalMinBox.ActualWidth, window.GlobalMaxBox.ActualWidth, 1);
                Assert.True(window.GlobalUnlimitedCheck.ActualWidth > 40);
                Assert.True(
                    window.GlobalUnlimitedCheck.TranslatePoint(new WpfPoint(0, 0), group).Y
                    < window.GlobalMaxLabel.TranslatePoint(new WpfPoint(0, 0), group).Y);
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

                try
                {
                    Directory.Delete(root, true);
                }
                catch
                {
                    // leftover
                }
            }
        });
    }

    [Fact]
    public void TypesFitsToOverallColumn_ExpandRevealsRestWithoutEllipsis()
    {
        WpfSta.Run(() =>
        {
            WpfSta.EnsureApp();
            Window? window = null;
            try
            {
                var typeSummary =
                    "Video: 12,632 files, 2.93 TB (82%); Images: 126 files, 451 MB (<1%); Archives: 515 files, 120 GB (3%); Other: 51,102 files, 80.0 GB (14%)";
                var stats = ProgressStats.From(
                    new JobProgress
                    {
                        Status = JobStatus.Copying,
                        StageName = "Copying",
                        StageIndex = 2,
                        StageCount = 4,
                        CurrentFile = @"Warren Truss\compressed\warren.zip",
                        FilesCopied = 43631,
                        FilesTotal = 64375,
                        BytesCopied = 43L * 1024 * 1024 * 1024,
                        BytesTotal = 811L * 1024 * 1024 * 1024,
                        BytesPerSecond = 6 * 1024 * 1024,
                        StartedUtc = DateTimeOffset.UtcNow.AddHours(-3),
                        StageStartedUtc = DateTimeOffset.UtcNow.AddMinutes(-25),
                        TypeSummary = typeSummary
                    },
                    jobIndex: 5,
                    jobCount: 5,
                    overall: new JobProgress { FilesCopied = 46346, FilesTotal = 142498 });

                var line = new ProgressStatsLine
                {
                    DataContext = stats,
                    Width = 980
                };
                window = new Window
                {
                    Width = 1000,
                    Height = 280,
                    Content = line
                };
                window.Show();
                WpfSta.Flush();
                line.UpdateLayout();
                WpfSta.Flush();

                var types = line.TypesPair;
                Assert.Equal(Visibility.Visible, types.Visibility);
                Assert.Equal(TextTrimming.None, types.ValueText.TextTrimming);
                Assert.Equal(TextWrapping.NoWrap, types.ValueText.TextWrapping);
                Assert.DoesNotContain("...", types.ValueRun.Text, StringComparison.Ordinal);
                Assert.Contains("Video", types.ValueRun.Text, StringComparison.Ordinal);
                Assert.Contains("82%", types.ValueRun.Text, StringComparison.Ordinal);
                Assert.Equal(Visibility.Visible, types.MoreToggle.Visibility);
                Assert.Equal("more", types.MoreLabel.Text);
                Assert.False(types.IsTypesExpanded);

                var pairs = FindVisualChildren<StatPairText>(line).ToArray();
                var overall = Pair(pairs, "Overall files");
                var file = Pair(pairs, "File");
                var typesRight = RightX(types, line);
                var fileLeft = LeftX(file, line);
                var overallBottom = overall.TransformToAncestor(line)
                    .Transform(new WpfPoint(0, overall.ActualHeight)).Y;
                var typesTop = types.TransformToAncestor(line).Transform(new WpfPoint(0, 0)).Y;
                Assert.True(
                    typesTop + 1 >= overallBottom,
                    "Types sits on the next row so it does not paint over Overall files");
                Assert.True(
                    typesRight <= fileLeft + 1.5,
                    $"Types (right {typesRight}) must stop before the File column (left {fileLeft}), not overlap later stats");

                var collapsedHeight = types.ActualHeight;
                types.MoreToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                WpfSta.Flush();
                types.UpdateLayout();
                types.TypesTable.UpdateLayout();
                line.UpdateLayout();
                WpfSta.Flush();

                Assert.True(types.IsTypesExpanded);
                Assert.Equal("less", types.MoreLabel.Text);
                Assert.Equal(Visibility.Collapsed, types.ValueText.Visibility);
                Assert.Equal(Visibility.Visible, types.TypesTable.Visibility);
                Assert.Equal(TextWrapping.NoWrap, types.ValueText.TextWrapping);

                var typeRows = TypeRows(types);
                Assert.Equal(4, typeRows.Length);
                Assert.Equal(["Video", "Images", "Archives", "Other"], typeRows.Select(r => TypeCell(r, "TypeName").Text).ToArray());
                Assert.Equal("12,632 files", TypeCell(typeRows[0], "TypeFiles").Text);
                Assert.Equal("126 files", TypeCell(typeRows[1], "TypeFiles").Text);
                Assert.Equal("2.93 TB", TypeCell(typeRows[0], "TypeSize").Text);
                Assert.Equal("451 MB", TypeCell(typeRows[1], "TypeSize").Text);
                Assert.Equal("82%", TypeCell(typeRows[0], "TypeShare").Text);
                Assert.Equal("<1%", TypeCell(typeRows[1], "TypeShare").Text);
                Assert.DoesNotContain("...", TypeCell(typeRows[2], "TypeName").Text, StringComparison.Ordinal);

                Assert.Equal(TextAlignment.Left, TypeCell(typeRows[0], "TypeName").TextAlignment);
                Assert.Equal(TextAlignment.Right, TypeCell(typeRows[0], "TypeFiles").TextAlignment);
                Assert.Equal(TextAlignment.Right, TypeCell(typeRows[0], "TypeSize").TextAlignment);
                Assert.Equal(TextAlignment.Right, TypeCell(typeRows[0], "TypeShare").TextAlignment);

                AssertAligned(typeRows.Select(r => LeftX(TypeCell(r, "TypeName"), line)), "type names left-aligned");
                AssertAligned(typeRows.Select(r => RightX(TypeCell(r, "TypeFiles"), line)), "file counts right-aligned");
                AssertAligned(typeRows.Select(r => RightX(TypeCell(r, "TypeSize"), line)), "sizes right-aligned");
                AssertAligned(typeRows.Select(r => RightX(TypeCell(r, "TypeShare"), line)), "shares right-aligned");

                var nameToFiles = ColumnGap(typeRows[0], "TypeName", "TypeFiles", line);
                var filesToSize = ColumnGap(typeRows[0], "TypeFiles", "TypeSize", line);
                var sizeToShare = ColumnGap(typeRows[0], "TypeSize", "TypeShare", line);
                Assert.InRange(nameToFiles, 8, 24);
                Assert.True(Math.Abs(nameToFiles - filesToSize) < 1.5, "column gutters must match");
                Assert.True(Math.Abs(filesToSize - sizeToShare) < 1.5, "column gutters must match");
                foreach (var row in typeRows.Skip(1))
                {
                    Assert.True(Math.Abs(ColumnGap(row, "TypeName", "TypeFiles", line) - nameToFiles) < 1.5);
                    Assert.True(Math.Abs(ColumnGap(row, "TypeFiles", "TypeSize", line) - filesToSize) < 1.5);
                    Assert.True(Math.Abs(ColumnGap(row, "TypeSize", "TypeShare", line) - sizeToShare) < 1.5);
                }

                Assert.True(types.ActualHeight > collapsedHeight + 4, "expanded Types should grow this row only");

                var job = Pair(pairs, "Job");
                Assert.True(job.ActualHeight < types.ActualHeight, "UniformGrid rows must not stretch with Types expand");

                types.MoreToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                WpfSta.Flush();
                types.UpdateLayout();
                WpfSta.Flush();

                Assert.False(types.IsTypesExpanded);
                Assert.Equal("more", types.MoreLabel.Text);
                Assert.Equal(Visibility.Visible, types.ValueText.Visibility);
                Assert.Equal(Visibility.Collapsed, types.TypesTable.Visibility);
                Assert.Equal(TextWrapping.NoWrap, types.ValueText.TextWrapping);
                Assert.DoesNotContain("...", types.ValueRun.Text, StringComparison.Ordinal);
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

    private static Grid[] TypeRows(StatPairText types) =>
        FindVisualChildren<Grid>(types.TypesTable)
            .Where(g => g.Children.OfType<TextBlock>().Any(t => Equals(t.Tag, "TypeName")))
            .ToArray();

    private static TextBlock TypeCell(Grid row, string tag) =>
        Assert.Single(row.Children.OfType<TextBlock>(), t => Equals(t.Tag, tag));

    private static void AssertAligned(IEnumerable<double> xs, string because)
    {
        var values = xs.ToArray();
        Assert.True(values.Length >= 2, because);
        Assert.True(values.Max() - values.Min() < 1.5, because);
    }

    private static double ColumnGap(Grid row, string leftTag, string rightTag, Visual ancestor) =>
        LeftX(TypeCell(row, rightTag), ancestor) - RightX(TypeCell(row, leftTag), ancestor);

    private static double LeftX(FrameworkElement element, Visual ancestor) =>
        element.TransformToAncestor(ancestor).Transform(new WpfPoint(0, 0)).X;

    private static double RightX(FrameworkElement element, Visual ancestor) =>
        element.TransformToAncestor(ancestor).Transform(new WpfPoint(element.ActualWidth, 0)).X;

    private static double TopY(FrameworkElement element, Visual ancestor) =>
        element.TransformToAncestor(ancestor).Transform(new WpfPoint(0, 0)).Y;

    private static double BottomY(FrameworkElement element, Visual ancestor) =>
        element.TransformToAncestor(ancestor).Transform(new WpfPoint(0, element.ActualHeight)).Y;

    private static bool InsideVertically(FrameworkElement inner, FrameworkElement outer, Visual ancestor) =>
        TopY(inner, ancestor) + 1 >= TopY(outer, ancestor)
        && BottomY(inner, ancestor) <= BottomY(outer, ancestor) + 1;

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
