using System.Diagnostics;
using System.Runtime.InteropServices;

namespace U3DViewer.Viewer;

internal enum AgentProcessStatus
{
    NotDetected,
    Ready,
    Busy
}

internal sealed class UnityProcessInfo
{
    public required int ProcessId { get; init; }
    public required string ProcessName { get; init; }
    public required string ExecutablePath { get; init; }
    public required string Backend { get; init; }
    public required string PipeName { get; init; }
    public required AgentProcessStatus AgentStatus { get; init; }

    public string AgentStatusText => AgentStatus switch
    {
        AgentProcessStatus.Ready => "Ready",
        AgentProcessStatus.Busy => "Busy",
        _ => "Not detected"
    };
}

internal static class UnityProcessDiscovery
{
    private const int ErrorSemTimeout = 121;
    private const int ErrorPipeBusy = 231;

    public static IReadOnlyList<UnityProcessInfo> Scan()
    {
        var result = new List<UnityProcessInfo>();

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var executablePath = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(executablePath) ||
                    !TryInspectExecutable(executablePath, out var backend))
                {
                    continue;
                }

                var pipeName = $"u3d-viewer-{process.Id}";
                result.Add(new UnityProcessInfo
                {
                    ProcessId = process.Id,
                    ProcessName = Path.GetFileName(executablePath),
                    ExecutablePath = executablePath,
                    Backend = backend,
                    PipeName = pipeName,
                    AgentStatus = ProbeAgent(pipeName)
                });
            }
            catch
            {
                // Some protected/system processes cannot expose MainModule. Ignore them.
            }
            finally
            {
                process.Dispose();
            }
        }

        return result
            .OrderBy(item => item.AgentStatus == AgentProcessStatus.Ready ? 0 : item.AgentStatus == AgentProcessStatus.Busy ? 1 : 2)
            .ThenBy(item => item.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ProcessId)
            .ToArray();
    }

    public static bool TryInspectExecutable(string executablePath, out string backend)
    {
        backend = "Unknown";
        if (!File.Exists(executablePath) ||
            !string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        var executableName = Path.GetFileNameWithoutExtension(executablePath);
        var dataDirectory = Path.Combine(directory, executableName + "_Data");
        var unityPlayer = Path.Combine(directory, "UnityPlayer.dll");
        var globalGameManagers = Path.Combine(dataDirectory, "globalgamemanagers");

        // Requiring the executable-specific _Data directory avoids false positives
        // such as UnityCrashHandler64.exe living beside UnityPlayer.dll.
        if (!Directory.Exists(dataDirectory) ||
            (!File.Exists(unityPlayer) && !File.Exists(globalGameManagers)))
        {
            return false;
        }

        backend = DetectBackend(directory, dataDirectory);
        return true;
    }

    private static string DetectBackend(string gameDirectory, string dataDirectory)
    {
        if (File.Exists(Path.Combine(gameDirectory, "GameAssembly.dll")))
        {
            return "IL2CPP";
        }

        if (Directory.Exists(Path.Combine(gameDirectory, "MonoBleedingEdge")) ||
            Directory.Exists(Path.Combine(gameDirectory, "Mono")) ||
            File.Exists(Path.Combine(dataDirectory, "Managed", "Assembly-CSharp.dll")))
        {
            return "Mono";
        }

        return "Unknown";
    }

    private static AgentProcessStatus ProbeAgent(string pipeName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return AgentProcessStatus.NotDetected;
        }

        var path = $@"\\.\pipe\{pipeName}";
        if (WaitNamedPipe(path, 0))
        {
            return AgentProcessStatus.Ready;
        }

        var error = Marshal.GetLastWin32Error();
        return error is ErrorSemTimeout or ErrorPipeBusy
            ? AgentProcessStatus.Busy
            : AgentProcessStatus.NotDetected;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WaitNamedPipe(string lpNamedPipeName, uint nTimeOut);
}

internal static class ViewerSession
{
    public static UnityProcessInfo? Target { get; set; }
}
