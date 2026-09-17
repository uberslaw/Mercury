using System.Windows;
using System.Windows.Threading;

namespace Mercury;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandled;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandled;
        TaskScheduler.UnobservedTaskException += OnUnobservedTask;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            var paths = new AppPaths();
            ThemeService.Initialize(paths);
            base.OnStartup(e);
        }
        catch (Exception ex)
        {
            ReportFatal("startup", ex);
            Shutdown(1);
        }
    }

    private static void OnDispatcherUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (ThemeService.IsApplying)
        {
            ThemeService.Log("Unhandled dispatcher exception during theme apply", e.Exception);
            e.Handled = true;
            return;
        }

        ReportFatal("dispatcher", e.Exception);
        e.Handled = true;
        Current?.Shutdown(1);
    }

    private static void OnDomainUnhandled(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            ReportFatal("unhandled", ex);
        }
    }

    private static void OnUnobservedTask(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        ReportFatal("task", e.Exception);
        e.SetObserved();
    }

    private static void ReportFatal(string source, Exception ex)
    {
        string? logFile = null;
        try
        {
            var paths = new AppPaths();
            MercuryErrorLog.Write(paths, source, ex.Message, ex);
            logFile = paths.ErrorLogFile();
        }
        catch
        {
            // never throw from the crash reporter
        }

        try
        {
            var detail = $"{ex.GetType().Name}: {ex.Message}";
            if (ex.InnerException is not null)
            {
                detail += $"{Environment.NewLine}{ex.InnerException.GetType().Name}: {ex.InnerException.Message}";
            }

            var logLine = string.IsNullOrWhiteSpace(logFile)
                ? "A log may be under %APPDATA%\\Mercury\\logs\\"
                : "Log: " + logFile;

            MessageBox.Show(
                "Mercury could not start." + Environment.NewLine + Environment.NewLine
                + detail + Environment.NewLine + Environment.NewLine + logLine,
                "Mercury",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // last resort: stay silent only if the OS cannot show a dialog
        }
    }
}
