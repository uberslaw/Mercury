using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WpfPoint = System.Windows.Point;

namespace Mercury.Wpf.Tests;

[Collection("WpfSta")]
public class PathAndTreeUiTests
{
    [Fact]
    public void PathsEditable_IdleAndPaused_True_RunningCopy_False()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-paths-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        MainViewModel? vm = null;
        try
        {
            vm = new MainViewModel(new AppPaths(root));
            Assert.True(vm.HasQueueJobs == false);
            Assert.True(vm.PathsEditable);
            Assert.True(vm.BrowseSourceCommand.CanExecute(null));

            vm.IsRunning = true;
            vm.IsPaused = false;
            Assert.False(vm.PathsEditable);
            Assert.False(vm.BrowseSourceCommand.CanExecute(null));
            Assert.False(vm.BrowseDestCommand.CanExecute(null));

            vm.IsPaused = true;
            Assert.True(vm.PathsEditable);
            Assert.True(vm.BrowseSourceCommand.CanExecute(null));

            vm.IsRunning = false;
            vm.IsPaused = false;
            Assert.True(vm.PathsEditable);
        }
        finally
        {
            vm?.Dispose();
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
                // temp leftover is OK
            }
        }
    }

    [Fact]
    public void QueueBrowseStaysEnabledWhileRunning()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-qpaths-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        MainViewModel? vm = null;
        try
        {
            vm = new MainViewModel(new AppPaths(root));
            vm.IsRunning = true;
            vm.IsPaused = false;
            Assert.False(vm.PathsEditable);
            Assert.True(vm.BrowseQueueSourceCommand.CanExecute(null));
            Assert.True(vm.BrowseQueueDestCommand.CanExecute(null));
            vm.QueueSourcePath = @"D:\src";
            vm.QueueDestPath = @"E:\dst";
            Assert.True(vm.HasQueuePaths);
            Assert.True(vm.QueueAddToQueueCommand.CanExecute(null));
        }
        finally
        {
            vm?.Dispose();
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
                // temp leftover is OK
            }
        }
    }

    [Fact]
    public void QueueAddUsesDraftOptionsNotTransferAndWrapsByDefault()
    {
        WpfSta.Run(() =>
        {
            WpfSta.EnsureApp();
            var root = Path.Combine(Path.GetTempPath(), "mercury-qopt-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var src = Path.Combine(root, "Anchor Span");
            var dest = Path.Combine(root, "EngA data drive");
            Directory.CreateDirectory(src);
            Directory.CreateDirectory(dest);
            MainViewModel? vm = null;
            try
            {
                vm = new MainViewModel(new AppPaths(root));
                Assert.True(vm.IncludeSourceFolderName);
                Assert.True(vm.QueueDraft.IncludeSourceFolderName);
                vm.DryRun = true;
                vm.SourcePath = src;
                vm.DestPath = dest;
                Assert.Contains("Anchor Span", vm.LandingPreview, StringComparison.OrdinalIgnoreCase);
                vm.PrepareQueueForm();
                Assert.Equal(src, vm.QueueSourcePath);
                Assert.Contains("Anchor Span", vm.QueueLandingPreview, StringComparison.OrdinalIgnoreCase);
                vm.QueueDraft.DryRun = false;
                vm.QueueDraft.IncludeSourceFolderName = true;
                Assert.True(vm.QueueAddToQueueCommand.CanExecute(null));
                vm.QueueAddToQueueCommand.Execute(null);
                var queued = Assert.Single(vm.QueueJobs);
                Assert.False(queued.Job.Options.DryRun);
                Assert.True(queued.Job.Options.IncludeSourceFolderName);
                Assert.DoesNotContain("Contents only", queued.OptionBadges);
                queued.Job.Options.IncludeSourceFolderName = false;
                queued.RaiseComputed();
                Assert.Contains("Contents only", queued.OptionBadges);
            }
            finally
            {
                vm?.Dispose();
                try
                {
                    Directory.Delete(root, true);
                }
                catch
                {
                    // temp leftover is OK
                }
            }
        });
    }

    [Fact]
    public void AddingSecondSourceDoesNotWipeFirst()
    {
        WpfSta.Run(() =>
        {
            WpfSta.EnsureApp();
            var root = Path.Combine(Path.GetTempPath(), "mercury-multisrc-" + Guid.NewGuid().ToString("N"));
            var a = Path.Combine(root, "Photos");
            var b = Path.Combine(root, "Videos");
            var dest = Path.Combine(root, "dest");
            Directory.CreateDirectory(a);
            Directory.CreateDirectory(b);
            Directory.CreateDirectory(dest);
            MainViewModel? vm = null;
            try
            {
                vm = new MainViewModel(new AppPaths(root));
                vm.SourcePath = a;
                vm.AddSourceCommand.Execute(null);
                vm.SourcePath = b;
                vm.AddSourceCommand.Execute(null);
                Assert.Equal(2, vm.SourceFolders.Count);
                Assert.Contains(vm.SourceFolders, f => f.Path.Equals(a, StringComparison.OrdinalIgnoreCase));
                Assert.Contains(vm.SourceFolders, f => f.Path.Equals(b, StringComparison.OrdinalIgnoreCase));
                vm.DestPath = dest;
                Assert.True(vm.HasPaths);
                Assert.Contains("Photos", vm.LandingPreview, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("Videos", vm.LandingPreview, StringComparison.OrdinalIgnoreCase);
                vm.SourcePath = a;
                vm.AddSourceCommand.Execute(null);
                Assert.Equal(2, vm.SourceFolders.Count);
            }
            finally
            {
                vm?.Dispose();
                try { Directory.Delete(root, true); } catch { /* leftover */ }
            }
        });
    }

    [Fact]
    public void EmptySourceListCannotStart()
    {
        WpfSta.Run(() =>
        {
            WpfSta.EnsureApp();
            var root = Path.Combine(Path.GetTempPath(), "mercury-empty-src-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            MainViewModel? vm = null;
            try
            {
                vm = new MainViewModel(new AppPaths(root));
                vm.DestPath = Path.Combine(root, "dest");
                Assert.False(vm.HasPaths);
                Assert.False(vm.HasSourceFolders);
            }
            finally
            {
                vm?.Dispose();
                try { Directory.Delete(root, true); } catch { /* leftover */ }
            }
        });
    }

    [Fact]
    public void DestTypeCombo_SizesToSelectionNotFullWidth()
    {
        WpfSta.Run(() =>
        {
            WpfSta.EnsureApp();
            var combo = new ComboBox();
            combo.Items.Add(new ComboBoxItem { Content = "Folder" });
            combo.Items.Add(new ComboBoxItem { Content = "Catcher" });
            combo.SelectedIndex = 1;
            ComboBoxFit.SetToContent(combo, true);

            var host = new Grid { Width = 800, Height = 80 };
            host.Children.Add(combo);
            var window = new Window
            {
                Width = 840,
                Height = 120,
                Content = host
            };
            try
            {
                window.Show();
                WpfSta.Flush();
                ComboBoxFit.FitToSelection(combo);
                WpfSta.Flush();

                Assert.True(combo.ActualWidth > 40);
                Assert.True(combo.ActualWidth < 200);
                Assert.True(combo.ActualWidth < host.ActualWidth / 2);
                Assert.Equal("Catcher", ((ComboBoxItem)combo.SelectedItem).Content);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void FolderTreeHeader_SitsAboveFirstRow_NotOverData()
    {
        WpfSta.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "mercury-tree-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            MainViewModel? vm = null;
            Window? window = null;
            try
            {
                WpfSta.EnsureApp();
                vm = new MainViewModel(new AppPaths(root));
                var nodes = FolderTree.Build(
                    [
                        new FileRecord
                        {
                            RelativePath = @"day1\a.jpg",
                            Size = 10,
                            Status = FileCopyStatus.Copied
                        },
                        new FileRecord
                        {
                            RelativePath = @"day1\sub\b.jpg",
                            Size = 10,
                            Status = FileCopyStatus.Pending
                        }
                    ],
                    bytesPerSecond: 0);
                foreach (var node in nodes)
                {
                    vm.FolderTree.Add(new FolderTreeItem(node, new HashSet<string>()));
                }

                vm.FolderTreeEmpty = false;

                var panel = new FolderTreePanel
                {
                    DataContext = vm,
                    Width = 640,
                    Height = 360
                };
                window = new Window
                {
                    Width = 680,
                    Height = 420,
                    Content = panel
                };
                window.Show();
                WpfSta.Flush();

                var header = panel.Header;
                var tree = panel.Tree;
                Assert.True(header.ActualHeight > 8);
                Assert.True(tree.ActualHeight > 8);

                var headerBottom = header.TranslatePoint(new WpfPoint(0, header.ActualHeight), panel).Y;
                var treeTop = tree.TranslatePoint(new WpfPoint(0, 0), panel).Y;
                Assert.True(treeTop >= headerBottom - 1, $"tree top {treeTop} under header bottom {headerBottom}");

                var first = FindTreeItem(tree);
                Assert.NotNull(first);
                var firstTop = first!.TranslatePoint(new WpfPoint(0, 0), panel).Y;
                Assert.True(firstTop >= headerBottom - 1, $"first row {firstTop} overlaps header {headerBottom}");
                Assert.True(first.ActualHeight > 8);
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

                vm?.Dispose();
                try
                {
                    Directory.Delete(root, true);
                }
                catch
                {
                    // temp leftover is OK
                }
            }
        });
    }

    [Fact]
    public void QueueTileShowsStartAndPending()
    {
        var item = new QueueJobItem(new Job
        {
            Name = "lab",
            SourcePath = @"D:\src",
            DestinationPath = @"E:\dst",
            Status = JobStatus.Pending
        });
        Assert.Equal("Pending", item.TileStatus);
        Assert.Equal("Start", item.ResumeLabel);
        Assert.True(item.CanResume);
        Assert.True(item.ResumeCommand.CanExecute(null));
        Assert.Equal("#1", item.OrderText);
        Assert.Contains("D:\\src", item.Route, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("E:\\dst", item.Route, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void QueueResume_Incomplete_ExecutesAndChipGoesPreparing()
    {
        var started = false;
        var item = new QueueJobItem(
            new Job { Name = "anchor", Status = JobStatus.Incomplete },
            onResume: _ => started = true);
        Assert.Equal("Resume", item.ResumeLabel);
        Assert.True(item.CanResume);
        Assert.True(item.ResumeCommand.CanExecute(null));
        Assert.Contains("Resume this job", item.ResumeToolTip, StringComparison.Ordinal);

        item.ResumeCommand.Execute(null);

        Assert.True(started);
        Assert.True(item.IsStarting);
        Assert.Equal("Preparing", item.TileStatus);
        Assert.Equal(QueueStatusTone.Transfer, item.StatusTone);
        Assert.False(item.CanResume);
        Assert.False(item.ResumeCommand.CanExecute(null));
        Assert.Contains("Starting this job", item.ResumeToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public void QueueResume_Completed_DisabledWithReason()
    {
        var started = false;
        var item = new QueueJobItem(
            new Job { Status = JobStatus.Completed },
            onResume: _ => started = true);
        Assert.False(item.CanResume);
        Assert.False(item.ResumeCommand.CanExecute(null));
        Assert.Contains("finished", item.ResumeToolTip, StringComparison.OrdinalIgnoreCase);
        item.ResumeCommand.Execute(null);
        Assert.False(started);
        Assert.Equal("Done", item.TileStatus);
    }

    [Fact]
    public void ResumeQueueJobCommand_IncompleteItem_MarksStarting()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-qresume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        MainViewModel? vm = null;
        try
        {
            vm = new MainViewModel(new AppPaths(root));
            var item = new QueueJobItem(new Job
            {
                Name = "span",
                Status = JobStatus.Incomplete
            });
            Assert.True(vm.ResumeQueueJobCommand.CanExecute(item));
            vm.ResumeQueueJobCommand.Execute(item);
            Assert.True(item.IsStarting);
            Assert.Equal("Preparing", item.TileStatus);
            Assert.False(vm.ResumeQueueJobCommand.CanExecute(item));
        }
        finally
        {
            vm?.Dispose();
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
                // leftover
            }
        }
    }

    [Fact]
    public void OpenLogCommand_LoadsJobFileIntoConsole()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-openlog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        MainViewModel? vm = null;
        try
        {
            var paths = new AppPaths(root);
            Directory.CreateDirectory(paths.Logs);
            var jobId = "joblog1";
            File.WriteAllText(paths.JobLogFile(jobId), "2026-09-23T00:00:00.0000000Z [Error] 25423 issues\ninfo line\n");
            vm = new MainViewModel(paths);
            var selected = false;
            vm.SelectConsoleRequested = () => selected = true;
            Assert.True(vm.OpenLogCommand.CanExecute(jobId));
            vm.OpenLogCommand.Execute(jobId);
            Assert.True(selected);
            Assert.Contains(vm.ConsoleLines, l => l.IsError && l.Text.Contains("25423 issues", StringComparison.Ordinal));
            Assert.Contains(vm.ConsoleLines, l => !l.IsError && l.Text.Contains("info line", StringComparison.Ordinal));
        }
        finally
        {
            vm?.Dispose();
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
                // leftover
            }
        }
    }

    [Fact]
    public void HistoryItem_CanOpenLog_WhenMessageMentionsLogOrFileExists()
    {
        Assert.True(new HistoryItem(new TransferHistoryEntry
        {
            ResultMessage = "Incomplete — 25423 issue(s). Open the log for details."
        }).CanOpenLog);
        Assert.False(new HistoryItem(new TransferHistoryEntry
        {
            ResultMessage = "Verified complete."
        }).CanOpenLog);
        Assert.True(new HistoryItem(new TransferHistoryEntry
        {
            ResultMessage = "Verified complete."
        }, logExists: true).CanOpenLog);
    }

    [Fact]
    public void QueueTilePhaseLabelsAreTransferringVerifyingRundown()
    {
        var copying = new QueueJobItem(new Job { Name = "copy", Status = JobStatus.Copying });
        Assert.Equal("Transferring", copying.TileStatus);
        Assert.Equal(QueueStatusTone.Transfer, copying.StatusTone);

        var verifying = new QueueJobItem(new Job { Name = "v", Status = JobStatus.Verifying });
        verifying.ApplyProgress(new JobProgress
        {
            Status = JobStatus.Verifying,
            StageName = "Verifying",
            Message = "Verifying…",
            RundownDone = 1,
            RundownTotal = 10,
            Eta = null
        });
        Assert.Equal("Verifying", verifying.TileStatus);
        Assert.Equal(QueueStatusTone.Verify, verifying.StatusTone);
        Assert.False(verifying.CanPause);
        Assert.True(verifying.IsActive);

        var rundown = new QueueJobItem(new Job { Name = "r", Status = JobStatus.Incomplete });
        rundown.ApplyProgress(new JobProgress
        {
            Status = JobStatus.Incomplete,
            StageName = CopyPipeline.RundownLabel,
            Message = CopyPipeline.RundownMessage,
            RundownDone = 2,
            RundownTotal = 10
        });
        Assert.Equal("Rundown", rundown.TileStatus);
        Assert.True(rundown.IsActive);
        Assert.Equal(QueueStatusTone.Verify, rundown.StatusTone);
        Assert.Equal(QueueStatusHighlight.VerifyBrushKey, QueueStatusHighlight.BrushKey(rundown.StatusTone));
    }

    [Fact]
    public void QueueStatusHighlight_MapsTileStatusesToTones()
    {
        Assert.Equal(QueueStatusTone.Transfer, new QueueJobItem(new Job { Status = JobStatus.Copying }).StatusTone);
        Assert.Equal(QueueStatusTone.Transfer, new QueueJobItem(new Job { Status = JobStatus.Preparing }).StatusTone);
        Assert.Equal(QueueStatusTone.Transfer, new QueueJobItem(new Job { Status = JobStatus.Enumerating }).StatusTone);
        Assert.Equal(QueueStatusTone.Verify, new QueueJobItem(new Job { Status = JobStatus.Verifying }).StatusTone);
        Assert.Equal(QueueStatusTone.Complete, new QueueJobItem(new Job { Status = JobStatus.Completed }).StatusTone);
        Assert.Equal(QueueStatusTone.Error, new QueueJobItem(new Job { Status = JobStatus.Incomplete }).StatusTone);
        Assert.Equal(QueueStatusTone.Error, new QueueJobItem(new Job { Status = JobStatus.Failed }).StatusTone);
        Assert.Equal(QueueStatusTone.Paused, new QueueJobItem(new Job { Status = JobStatus.Paused }).StatusTone);
        Assert.Equal(QueueStatusTone.Paused, new QueueJobItem(new Job { Status = JobStatus.PausedOutsideHours }).StatusTone);
        Assert.Equal(QueueStatusTone.Queued, new QueueJobItem(new Job { Status = JobStatus.Pending }).StatusTone);
        Assert.Equal(QueueStatusTone.Queued, new QueueJobItem(new Job { Status = JobStatus.Pending, OnHold = true }).StatusTone);
        Assert.Equal(QueueStatusTone.Queued, new QueueJobItem(new Job { Status = JobStatus.Cancelled }).StatusTone);

        var pausing = new QueueJobItem(new Job { Name = "copy", Status = JobStatus.Copying })
        {
            PauseAfterArmed = true
        };
        pausing.RaiseComputed();
        Assert.Equal(QueueStatusHighlight.PauseAfterLabel, pausing.TileStatus);
        Assert.Equal(QueueStatusTone.PauseAfter, pausing.StatusTone);

        Assert.Equal(QueueStatusTone.Transfer, QueueStatusHighlight.ForLabel("Transferring"));
        Assert.Equal(QueueStatusTone.Transfer, QueueStatusHighlight.ForLabel("Copying"));
        Assert.Equal(QueueStatusTone.Transfer, QueueStatusHighlight.ForLabel("Running"));
        Assert.Equal(QueueStatusTone.Verify, QueueStatusHighlight.ForLabel("Rundown"));
        Assert.Equal(QueueStatusTone.Complete, QueueStatusHighlight.ForLabel("Done"));
        Assert.Equal(QueueStatusTone.Complete, QueueStatusHighlight.ForLabel("Finished"));
        Assert.Equal(QueueStatusTone.Error, QueueStatusHighlight.ForLabel("Failed"));
        Assert.Equal(QueueStatusTone.PauseAfter, QueueStatusHighlight.ForLabel(QueueStatusHighlight.PauseAfterLabel));
        Assert.Equal(QueueStatusTone.Queued, QueueStatusHighlight.ForLabel(QueueStatusHighlight.IdleLabel));
        Assert.Equal(QueueStatusTone.Queued, QueueStatusHighlight.ForLabel("Idle"));
        Assert.Equal("Paused", QueueStatusHighlight.ForRun(true, true, false, false, false, false, JobStatus.Copying));
        Assert.Equal(QueueStatusHighlight.IdleLabel, QueueStatusHighlight.ForRun(false, false, false, false, false, false, null));
        Assert.Equal("Verifying", QueueStatusHighlight.ForRun(false, false, false, true, true, false, JobStatus.Verifying));
        Assert.Equal("Rundown", QueueStatusHighlight.ForRun(false, false, false, true, false, true, JobStatus.Incomplete));
        Assert.Equal(QueueStatusHighlight.PauseAfterLabel, QueueStatusHighlight.ForRun(true, false, true, false, false, false, JobStatus.Copying));
        Assert.Equal("Transferring", QueueStatusHighlight.ForRun(true, false, false, false, false, false, JobStatus.Copying));
        Assert.Equal(QueueStatusHighlight.TransferBrushKey, QueueStatusHighlight.BrushKey(QueueStatusTone.Transfer));
        Assert.Equal(QueueStatusHighlight.VerifyBrushKey, QueueStatusHighlight.BrushKey(QueueStatusTone.Verify));
        Assert.Equal(QueueStatusHighlight.CompleteBrushKey, QueueStatusHighlight.BrushKey(QueueStatusTone.Complete));
        Assert.Equal(QueueStatusHighlight.ErrorBrushKey, QueueStatusHighlight.BrushKey(QueueStatusTone.Error));
        Assert.Equal(QueueStatusHighlight.PausedBrushKey, QueueStatusHighlight.BrushKey(QueueStatusTone.Paused));
        Assert.Equal(QueueStatusHighlight.PauseAfterBrushKey, QueueStatusHighlight.BrushKey(QueueStatusTone.PauseAfter));
        Assert.Equal(QueueStatusHighlight.QueuedBrushKey, QueueStatusHighlight.BrushKey(QueueStatusTone.Queued));
    }

    [Fact]
    public void QueueStatusChip_HighlightsWordNotWholeRow()
    {
        WpfSta.Run(() =>
        {
            WpfSta.EnsureApp();
            Window? window = null;
            try
            {
                var item = new QueueJobItem(new Job
                {
                    Name = "Warren Truss",
                    SourcePath = @"D:\Warren Truss",
                    DestinationPath = @"Z:\dest",
                    Status = JobStatus.Copying
                });
                var chip = new Border
                {
                    Style = (Style)System.Windows.Application.Current.FindResource("QueueStatusChip"),
                    DataContext = item,
                    Child = new TextBlock
                    {
                        Text = item.TileStatus,
                        FontWeight = FontWeights.SemiBold
                    }
                };
                var buttons = new WrapPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children =
                    {
                        new Button { Content = "Job Options", MinWidth = 88 }
                    }
                };
                var row = new DockPanel { Width = 720 };
                DockPanel.SetDock(buttons, Dock.Right);
                row.Children.Add(buttons);
                var body = new StackPanel();
                body.Children.Add(new TextBlock { Text = "#2  Warren Truss", FontWeight = FontWeights.SemiBold });
                body.Children.Add(chip);
                body.Children.Add(new ProgressBar { Value = 40, Maximum = 100, Height = 14 });
                row.Children.Add(body);

                window = new Window
                {
                    Width = 760,
                    Height = 180,
                    Content = row
                };
                window.Show();
                WpfSta.Flush();
                row.UpdateLayout();
                WpfSta.Flush();

                Assert.Equal("Transferring", item.TileStatus);
                Assert.Equal(QueueStatusTone.Transfer, item.StatusTone);
                Assert.Equal(HorizontalAlignment.Left, chip.HorizontalAlignment);
                Assert.True(chip.ActualWidth > 40, "chip should wrap the status word");
                Assert.True(chip.ActualWidth < 220, "chip must not stretch across the queue row");
                Assert.True(buttons.ActualWidth > 40);

                var fill = Assert.IsType<SolidColorBrush>(chip.Background);
                var expected = Assert.IsType<SolidColorBrush>(
                    System.Windows.Application.Current.FindResource(QueueStatusHighlight.TransferBrushKey));
                Assert.Equal(expected.Color, fill.Color);
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
    public void HeaderStatusChip_IdlePausedAndPauseAfter_UseQueueMapper()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-header-chip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        MainViewModel? vm = null;
        try
        {
            vm = new MainViewModel(new AppPaths(root));
            Assert.Equal(QueueStatusHighlight.IdleLabel, vm.HeaderStatusLabel);
            Assert.Equal(QueueStatusTone.Queued, vm.HeaderStatusTone);
            Assert.Equal(QueueStatusHighlight.IdleLabel, vm.HeaderStatusChip.TileStatus);
            Assert.Equal(QueueStatusTone.Queued, vm.HeaderStatusChip.StatusTone);

            vm.IsRunning = true;
            vm.IsPaused = true;
            Assert.Equal("Paused", vm.HeaderStatusLabel);
            Assert.Equal(QueueStatusTone.Paused, vm.HeaderStatusTone);
        }
        finally
        {
            vm?.Dispose();
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
                // leftover
            }
        }
    }

    [Fact]
    public void HeaderAndQueuePauseAfterChip_UsesAmberFill()
    {
        WpfSta.Run(() =>
        {
            WpfSta.EnsureApp();
            Window? window = null;
            try
            {
                var view = new StatusChipView(QueueStatusHighlight.PauseAfterLabel);
                Assert.Equal(QueueStatusTone.PauseAfter, view.StatusTone);
                var chip = new Border
                {
                    Style = (Style)System.Windows.Application.Current.FindResource("QueueStatusChip"),
                    DataContext = view,
                    Child = new TextBlock
                    {
                        Text = view.TileStatus,
                        FontWeight = FontWeights.SemiBold
                    }
                };
                window = new Window
                {
                    Width = 320,
                    Height = 80,
                    Content = chip
                };
                window.Show();
                WpfSta.Flush();
                chip.UpdateLayout();
                WpfSta.Flush();

                Assert.Equal(QueueStatusHighlight.PauseAfterLabel, ((TextBlock)chip.Child).Text);
                var fill = Assert.IsType<SolidColorBrush>(chip.Background);
                var expected = Assert.IsType<SolidColorBrush>(
                    System.Windows.Application.Current.FindResource(QueueStatusHighlight.PauseAfterBrushKey));
                Assert.Equal(expected.Color, fill.Color);
                Assert.NotEqual(
                    ((SolidColorBrush)System.Windows.Application.Current.FindResource(QueueStatusHighlight.PausedBrushKey)).Color,
                    fill.Color);
                Assert.NotEqual(
                    ((SolidColorBrush)System.Windows.Application.Current.FindResource(QueueStatusHighlight.TransferBrushKey)).Color,
                    fill.Color);
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
    public void JobOptionsForm_MbpsToggleStoresMegabytes()
    {
        var form = new JobOptionsForm
        {
            ShowSpeedInMegabits = true,
            UnlimitedSpeed = false,
            MaxMBpsText = "80"
        };
        Assert.Equal("Mbps", form.SpeedUnitLabel);
        Assert.Equal(10, form.ToOptions().MaxMegabytesPerSecond);

        form.LoadFrom(new JobOptions { MaxMegabytesPerSecond = 10 }, scheduledStart: null);
        Assert.Equal("80", form.MaxMBpsText);
        form.ShowSpeedInMegabits = false;
        Assert.Equal("MB/s", form.SpeedUnitLabel);
        Assert.Equal("10", form.MaxMBpsText);
        Assert.Equal(10, form.ToOptions().MaxMegabytesPerSecond);
    }

    [Fact]
    public void JobPathsPanel_SourceAndDestinationSections_AlignSelectorsAndButtons()
    {
        WpfSta.Run(() =>
        {
            WpfSta.EnsureApp();
            Window? window = null;
            try
            {
                var panel = new JobPathsPanel
                {
                    Width = 860,
                    SourcePath = @"D:\Anchor Span",
                    DestPath = @"Z:\BNE\Projects\313000\EngA data drive",
                    DestKindIndex = 0,
                    DestIsFolder = true,
                    DestIsCatcher = false,
                    HasSourceFolders = true,
                    SourceFolders = new[] { new SourceFolderItem(@"D:\Anchor Span") },
                    IncludeSourceFolderName = true,
                    LandingPreview = @"Will land in: Z:\BNE\Projects\313000\EngA data drive\Anchor Span",
                    ShowLandingPreview = true,
                    PathsEditable = true
                };
                window = new Window
                {
                    Width = 900,
                    Height = 420,
                    Content = panel
                };
                window.Show();
                WpfSta.Flush();
                panel.UpdateLayout();
                WpfSta.Flush();

                Assert.Equal("Source", panel.SourceGroup.Header);
                Assert.Equal("Destination", panel.DestinationGroup.Header);
                Assert.DoesNotContain(
                    FindVisualChildren<GroupBox>(panel),
                    g => string.Equals(g.Header as string, "Paths", StringComparison.Ordinal));

                Assert.Equal(HorizontalAlignment.Left, panel.SourcePathCombo.HorizontalContentAlignment);
                Assert.Equal(HorizontalAlignment.Left, panel.DestPathCombo.HorizontalContentAlignment);
                Assert.Equal(HorizontalAlignment.Left, panel.DestKindCombo.HorizontalContentAlignment);
                var sourceBox = FindDescendant<TextBox>(panel.SourcePathCombo);
                if (sourceBox is not null)
                {
                    Assert.Equal(TextAlignment.Left, sourceBox.TextAlignment);
                }

                var buttons = new[]
                {
                    panel.BrowseSourceButton,
                    panel.AddSourceButton,
                    panel.ClearSourcesButton,
                    panel.BrowseDestButton
                };
                var remove = Assert.Single(FindVisualChildren<Button>(panel.SourceRemoveList));
                Assert.Equal("Remove", remove.Content);
                var all = buttons.Append(remove).ToArray();
                Assert.True(all.All(b => Math.Abs(b.ActualWidth - all[0].ActualWidth) < 1.5), "path action buttons must share width");
                Assert.True(all.All(b => Math.Abs(b.ActualHeight - all[0].ActualHeight) < 1.5), "path action buttons must share height");
                Assert.True(all[0].ActualWidth >= 80);

                var browseX = LeftX(panel.BrowseSourceButton, panel);
                var destBrowseX = LeftX(panel.BrowseDestButton, panel);
                var removeX = LeftX(remove, panel);
                Assert.True(Math.Abs(browseX - destBrowseX) < 2.5, $"Browse buttons must share X ({browseX} vs {destBrowseX})");
                Assert.True(Math.Abs(browseX - removeX) < 2.5, $"Remove must stack under Browse ({browseX} vs {removeX})");
                var addX = LeftX(panel.AddSourceButton, panel);
                var clearX = LeftX(panel.ClearSourcesButton, panel);
                Assert.True(Math.Abs(addX - clearX) < 2.5, $"Clear must stack under Add ({addX} vs {clearX})");

                var sourceComboX = LeftX(panel.SourcePathCombo, panel);
                var destComboX = LeftX(panel.DestPathCombo, panel);
                Assert.True(Math.Abs(sourceComboX - destComboX) < 2.5, $"path combos must share left X ({sourceComboX} vs {destComboX})");
                Assert.True(Math.Abs(panel.SourcePathCombo.ActualWidth - panel.DestPathCombo.ActualWidth) < 3,
                    $"path combos must share width ({panel.SourcePathCombo.ActualWidth} vs {panel.DestPathCombo.ActualWidth})");

                var kindX = LeftX(panel.DestKindCombo, panel);
                Assert.True(kindX <= destComboX + 1, "Folder combo stays left, not shrinking the dest path");
                Assert.True(panel.DestKindCombo.ActualWidth < panel.DestPathCombo.ActualWidth / 2);

                Assert.True(IsAncestor(panel.DestinationGroup, panel.IncludeCheckBox));
                Assert.True(IsAncestor(panel.DestinationGroup, panel.LandingPreviewText));
                Assert.True(IsAncestor(panel.SourceGroup, panel.SourceFolderList));
                Assert.False(IsAncestor(panel.SourceGroup, panel.IncludeCheckBox));
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

    private static TreeViewItem? FindTreeItem(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TreeViewItem item)
            {
                return item;
            }

            var nested = FindTreeItem(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static double LeftX(FrameworkElement element, Visual ancestor) =>
        element.TransformToAncestor(ancestor).Transform(new WpfPoint(0, 0)).X;

    private static bool IsAncestor(DependencyObject ancestor, DependencyObject? node)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, ancestor))
            {
                return true;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return false;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in FindVisualChildren<T>(root))
        {
            return child;
        }

        return null;
    }

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
