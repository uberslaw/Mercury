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
        Assert.Equal("#1", item.OrderText);
        Assert.Contains("D:\\src", item.Route, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("E:\\dst", item.Route, StringComparison.OrdinalIgnoreCase);
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
}
