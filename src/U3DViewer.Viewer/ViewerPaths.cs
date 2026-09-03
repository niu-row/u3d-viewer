namespace U3DViewer.Viewer;

internal static class ViewerPaths
{
    private const string DataDirectoryName = "U3DViewer";

    public static string ViewerDataRoot =>
        Path.Combine(AppContext.BaseDirectory, DataDirectoryName);

    public static string ViewerLanguagePath =>
        Path.Combine(ViewerDataRoot, "language.txt");

    public static string ViewerLogsDirectory =>
        Path.Combine(ViewerDataRoot, "Logs");

    public static string GetGameDataRoot(string executablePath) =>
        Path.Combine(GetGameDirectory(executablePath), DataDirectoryName);

    public static string GetGameSettingsDirectory(string executablePath) =>
        Path.Combine(GetGameDataRoot(executablePath), "Settings");

    public static string GetSceneSettingsPath(string executablePath) =>
        Path.Combine(GetGameSettingsDirectory(executablePath), "scene.json");

    public static string GetGameDownloadsDirectory(string executablePath) =>
        Path.Combine(GetGameDataRoot(executablePath), "Downloads");

    public static string GetGameLogsDirectory(string executablePath) =>
        Path.Combine(GetGameDataRoot(executablePath), "Logs");

    public static string GetAgentCacheDirectory(string executablePath, string backend, string fingerprint) =>
        Path.Combine(GetGameDataRoot(executablePath), "AgentCache", backend, fingerprint);

    public static string GetAgentBuildWorkspace(string executablePath, string backend) =>
        Path.Combine(
            GetGameDataRoot(executablePath),
            "Temp",
            "AgentBuilder",
            $"{backend}-{Environment.ProcessId}");

    private static string GetGameDirectory(string executablePath)
    {
        var directory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException($"Could not resolve game directory from executable path: {executablePath}");
        }

        return directory;
    }
}
