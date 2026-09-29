using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Mercury.Wpf.Tests;

[Collection("WpfSta")]
public class QueueLayoutTests
{
    [Fact]
    public void QueueTab_ListTakesRemainingHeight_AndRowShowsOnOption()
    {
        WpfSta.Run(() =>
        {
            WpfSta.EnsureApp();
            var root = Path.Combine(Path.GetTempPath(), "mercury-queue-layout-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            MainWindow? window = null;
            MainViewModel? vm = null;
            try
            {
                var dest = Path.Combine(root, "EngA");
                Directory.CreateDirectory(dest);
                var paths = new AppPaths(root);
                QueueStore.Save(paths,
                [
                    new Job
                    {
                        Name = "Anchor Span",
                        SourcePath = Path.Combine(root, "Anchor Span"),
                        DestinationPath = dest,
                        Status = JobStatus.Cancelled,
                        Options = new JobOptions { DryRun = true }
                    }
                ]);

                vm = new MainViewModel(paths);
                window = new MainWindow(vm)
                {
                    Width = 1100,
                    Height = 780,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -4000,
                    Top = 0
                };
                window.Show();
                WpfSta.Flush();
                window.QueueTab.IsSelected = true;
                WpfSta.Flush();
                window.UpdateLayout();
                WpfSta.Flush();

                Assert.Empty(FindVisualChildren<JobPathsPanel>(window.QueueTab));
                Assert.True(window.QueueListHost.ActualHeight > 360,
                    $"queue list should own remaining height ({window.QueueListHost.ActualHeight})");
                Assert.True(window.QueueListHost.ActualHeight > window.QueueToolbar.ActualHeight * 3,
                    "list must be taller than the compact Add job toolbar");
                Assert.Contains(
                    FindVisualChildren<TextBlock>(window.QueueList),
                    t => (t.Text ?? "").Contains("Dry Run", StringComparison.Ordinal));
                Assert.Contains(
                    FindVisualChildren<TextBlock>(window.QueueList),
                    t => (t.Text ?? "").Contains("Unlimited speed", StringComparison.Ordinal));
                Assert.Contains(vm.QueueJobs[0].OptionBadges, b => b == "Dry Run");
            }
            finally
            {
                try { window?.Close(); } catch { /* window already disposed the VM */ }
                if (window is null)
                {
                    vm?.Dispose();
                }

                try { Directory.Delete(root, true); } catch { /* leftover */ }
            }
        });
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
