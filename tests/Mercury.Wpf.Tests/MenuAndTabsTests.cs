using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Mercury.Wpf.Tests;

[Collection("WpfSta")]
public class MenuAndTabsTests
{
    [Fact]
    public void OperationalTabsStayVisible_ThemeAndSettingsAreMenuWindows_OpenLogSelectsConsole()
    {
        WpfSta.Run(() =>
        {
            WpfSta.EnsureApp();
            var root = Path.Combine(Path.GetTempPath(), "mercury-menu-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            MainWindow? window = null;
            try
            {
                var paths = new AppPaths(root);
                Directory.CreateDirectory(paths.Logs);
                var jobId = "joblog-menu";
                File.WriteAllText(paths.JobLogFile(jobId), "2026-09-29T00:00:00.0000000Z [Error] incomplete log\n");
                var vm = new MainViewModel(paths);
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

                var visible = window.MainTabs.Items.OfType<TabItem>()
                    .Where(t => t.Visibility == Visibility.Visible)
                    .Select(t => (string)t.Header)
                    .ToArray();
                Assert.Equal(["Transfer", "Queue", "Tree", "Compare"], visible);
                Assert.Equal(Visibility.Collapsed, window.ConsoleTab.Visibility);
                Assert.Equal(Visibility.Collapsed, window.HistoryTab.Visibility);
                Assert.Equal(Visibility.Collapsed, window.NetworkTab.Visibility);
                Assert.DoesNotContain(window.MainTabs.Items.OfType<TabItem>(), t => Equals(t.Header, "Theme"));
                Assert.DoesNotContain(window.MainTabs.Items.OfType<TabItem>(), t => Equals(t.Header, "Settings"));
                Assert.DoesNotContain(window.MainTabs.Items.OfType<TabItem>(), t => Equals(t.Header, "Help"));
                Assert.True(window.TransferTab.IsSelected);
                Assert.NotNull(window.OptionsThemeMenu);
                Assert.NotNull(window.OptionsSettingsMenu);
                Assert.NotNull(window.ViewConsoleMenu);

                window.OptionsThemeMenu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                WpfSta.Flush();
                var theme = window.OwnedWindows.OfType<ThemeEditorWindow>().FirstOrDefault();
                Assert.NotNull(theme);
                Assert.True(theme!.IsVisible);
                Assert.True(window.TransferTab.IsSelected);

                window.OptionsSettingsMenu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                WpfSta.Flush();
                var settings = window.OwnedWindows.OfType<SettingsWindow>().FirstOrDefault();
                Assert.NotNull(settings);
                Assert.True(settings!.IsVisible);

                Assert.True(vm.OpenLogCommand.CanExecute(jobId));
                vm.OpenLogCommand.Execute(jobId);
                WpfSta.Flush();
                window.UpdateLayout();
                WpfSta.Flush();
                Assert.True(window.ConsoleTab.IsSelected);
                Assert.Contains(vm.ConsoleLines, l => l.IsError && l.Text.Contains("incomplete log", StringComparison.Ordinal));

                foreach (Window owned in window.OwnedWindows.Cast<Window>().ToArray())
                {
                    owned.Close();
                }
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
}
