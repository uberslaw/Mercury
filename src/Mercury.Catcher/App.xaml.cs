using System.Windows;
using System.Windows.Threading;
using Mercury;

namespace Mercury.Catcher;

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
            base.OnStartup(e);
        }
        catch (Exception ex)
        {
            ReportFatal("catcher-startup", ex);
            Shutdown(1);
        }
    }

    private static void OnDispatcherUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ReportFatal("catcher-dispatcher", e.Exception);
        e.Handled = true;
        Current?.Shutdown(1);
    }

    private static void OnDomainUnhandled(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            ReportFatal("catcher-unhandled", ex);
        }
    }

    private static void OnUnobservedTask(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        ReportFatal("catcher-task", e.Exception);
        e.SetObserved();
    }

    private static void ReportFatal(string source, Exception ex)
    {
        string? logFile = null;
        try
        {
            var paths = new AppPaths();
            MercuryErrorLog.Write(paths, source, ex.Message, ex, catcher: true);
            logFile = paths.CatcherErrorLogFile();
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
                "Mercury Catcher could not start." + Environment.NewLine + Environment.NewLine
                + detail + Environment.NewLine + Environment.NewLine + logLine,
                "Mercury Catcher",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // last resort: stay silent only if the OS cannot show a dialog
        }
    }
}
