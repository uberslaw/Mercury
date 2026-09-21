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
