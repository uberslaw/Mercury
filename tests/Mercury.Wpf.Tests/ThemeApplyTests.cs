using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using WpfApp = System.Windows.Application;
using WpfBinding = System.Windows.Data.Binding;
using WpfRect = System.Windows.Shapes.Rectangle;

namespace Mercury.Wpf.Tests;

[Collection("WpfSta")]
public class ThemeApplyTests
{
    [Fact]
    public void PickerApplyHex_SetsLiveTextBrushAndThemeRowSwatchBlue()
    {
        WpfSta.Run(() =>
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mercury-theme-apply-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Window? window = null;
            try
            {
                WpfSta.EnsureApp();
                ThemeService.InitializeForTests(new AppPaths(root));

                var vm = new ThemeViewModel();
                var item = vm.AllItems.First(i => i.Key == "TextBrush");

                var liveText = new TextBlock { Text = "live" };
                liveText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                var swatch = new WpfRect { Width = 36, Height = 26 };
                swatch.SetBinding(Shape.FillProperty, new WpfBinding(nameof(ThemeColorItem.SwatchBrush)) { Source = item });

                window = new Window
                {
                    Width = 240,
                    Height = 120,
                    Content = new StackPanel { Children = { liveText, swatch } }
                };
                window.Show();
                WpfSta.Flush();

                // Same method the Theme-tab color picker uses after OK.
                item.ApplyHex("#0000FF", normalize: true);
                WpfSta.Flush();

                var resource = Assert.IsType<SolidColorBrush>(WpfApp.Current.Resources["TextBrush"]);
                Assert.Equal(Colors.Blue, resource.Color);

                Assert.Equal(Colors.Blue, item.SwatchColor);
                var swatchSource = Assert.IsType<SolidColorBrush>(item.SwatchBrush);
                Assert.False(swatchSource.IsFrozen);
                Assert.Equal(Colors.Blue, swatchSource.Color);

                var rowFill = Assert.IsType<SolidColorBrush>(swatch.Fill);
                Assert.Equal(Colors.Blue, rowFill.Color);

                var liveBrush = Assert.IsType<SolidColorBrush>(liveText.Foreground);
                Assert.Equal(Colors.Blue, liveBrush.Color);
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
                    // temp leftover is OK
                }
            }
        });
    }
}

[CollectionDefinition("WpfSta")]
public sealed class WpfStaCollection : ICollectionFixture<WpfStaFixture>;

public sealed class WpfStaFixture;

internal static class WpfSta
{
    public static void Run(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    public static void EnsureApp()
    {
        if (WpfApp.Current is null)
        {
            var app = new App();
            app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }
    }

    public static void Flush() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}
