using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace U3DViewer.Viewer;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        ViewerLog.Initialize();

        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            var exception = eventArgs.ExceptionObject as Exception;
            ViewerLog.Error(
                $"Unhandled AppDomain exception. IsTerminating={eventArgs.IsTerminating}",
                exception);
        };

        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            ViewerLog.Error("Unobserved task exception.", eventArgs.Exception);
            eventArgs.SetObserved();
        };

        try
        {
            var lifetime = new ClassicDesktopStyleApplicationLifetime
            {
                Args = args
            };

            AppBuilder.Configure<Application>()
                .UsePlatformDetect()
                .AfterSetup(builder => builder.Instance?.Styles.Add(new FluentTheme()))
                .SetupWithLifetime(lifetime);

            ProcessPickerWindow? picker = null;
            picker = new ProcessPickerWindow(target =>
            {
                ViewerLog.Info($"Opening Viewer for {target.ProcessName} (PID {target.ProcessId}, {target.Backend}).");
                ViewerSession.Target = target;

                var mainWindow = new MainWindow
                {
                    Title = $"U3D Viewer — {target.ProcessName} ({target.ProcessId})"
                };

                try
                {
                    // Keep the picker as the lifetime main window until the Scene/Hierarchy window
                    // has actually completed its native-host initialization. If Show() fails, the
                    // picker remains a valid recovery surface instead of leaving the lifetime pointed
                    // at a window that never opened.
                    mainWindow.Show();
                    lifetime.MainWindow = mainWindow;
                    picker?.Close();
                }
                catch
                {
                    ViewerSession.Target = null;
                    try
                    {
                        mainWindow.Close();
                    }
                    catch
                    {
                        // Show may have failed before a platform window was fully created.
                    }
                    throw;
                }
            });

            lifetime.MainWindow = picker;
            var exitCode = lifetime.Start(args);
            ViewerLog.Info($"U3DViewer exited with code {exitCode}.");
            return exitCode;
        }
        catch (Exception ex)
        {
            ViewerLog.Error("Fatal Viewer startup/runtime error.", ex);
            return 1;
        }
    }
}
