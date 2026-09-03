using Avalonia;
using Avalonia.Controls;
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
            ShutdownMode = ShutdownMode.OnMainWindowClose
        };

        AppBuilder.Configure<Application>()
            .UsePlatformDetect()
            .AfterSetup(builder => builder.Instance?.Styles.Add(new FluentTheme()))
            .SetupWithLifetime(lifetime);

        lifetime.MainWindow = new MainWindow();
        return lifetime.Start(args);
    }
}
