using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace U3DViewer.Viewer;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var lifetime = new ClassicDesktopStyleApplicationLifetime
        {
            Args = args,
            ShutdownMode = ShutdownMode.OnLastWindowClose
        };

        AppBuilder.Configure<Application>()
            .UsePlatformDetect()
            .AfterSetup(builder => builder.Instance?.Styles.Add(new FluentTheme()))
            .SetupWithLifetime(lifetime);

        ProcessPickerWindow? picker = null;
        picker = new ProcessPickerWindow(target =>
        {
            ViewerSession.Target = target;

            var mainWindow = new MainWindow
            {
                Title = $"U3D Viewer — {target.ProcessName} ({target.ProcessId})"
            };

            lifetime.MainWindow = mainWindow;
            mainWindow.Show();
            picker?.Close();
        });

        lifetime.MainWindow = picker;
        return lifetime.Start(args);
    }
}
