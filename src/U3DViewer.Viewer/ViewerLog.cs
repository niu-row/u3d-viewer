using System.Text;

namespace U3DViewer.Viewer;

internal static class ViewerLog
{
    private static readonly object Sync = new();
    private static string? _logPath;

    public static string LogPath => _logPath ?? string.Empty;

    public static void Initialize()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "U3DViewer",
                "Logs");
            Directory.CreateDirectory(directory);
            _logPath = Path.Combine(
                directory,
                $"viewer-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
        }
        catch
        {
            _logPath = null;
        }

        Info("U3DViewer starting.");
        Info($"PID: {Environment.ProcessId}");
        Info($"Base directory: {AppContext.BaseDirectory}");
        if (!string.IsNullOrWhiteSpace(_logPath))
        {
            Info($"Log file: {_logPath}");
        }
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warning(string message) => Write("WARN", message, null);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var line = $"[{timestamp}] [{level}] {message}";
        var detail = exception is null ? null : exception.ToString();

        lock (Sync)
        {
            try
            {
                var writer = level == "ERROR" ? Console.Error : Console.Out;
                writer.WriteLine(line);
                if (detail is not null)
                {
                    writer.WriteLine(detail);
                }
            }
            catch
            {
                // Console logging must never break the Viewer.
            }

            if (string.IsNullOrWhiteSpace(_logPath))
            {
                return;
            }

            try
            {
                using var stream = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.WriteLine(line);
                if (detail is not null)
                {
                    writer.WriteLine(detail);
                }
            }
            catch
            {
                // File logging must never break the Viewer.
            }
        }
    }
}
