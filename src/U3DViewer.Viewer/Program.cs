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
                ViewerLog.BindToGame(target.ExecutablePath);
                ViewerLog.Info($"Opening Viewer for {target.ProcessName} (PID {target.ProcessId}, {target.Backend}).");
                ViewerSession.Target = target;

                var mainWindow = new MainWindow
                {
                    Title = $"U3D Viewer — {target.ProcessName} ({target.ProcessId})"
                };
                Localization.Attach(mainWindow);

                try
                {
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
                    }
                    throw;
                }
            });

            Localization.Attach(picker);
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
