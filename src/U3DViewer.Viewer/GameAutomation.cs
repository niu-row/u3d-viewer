using System.Diagnostics;

namespace U3DViewer.Viewer;

internal sealed record GameAutomationResult(bool Success, string Message, UnityProcessInfo? Target = null);

internal static class GameAutomation
{
    private static readonly TimeSpan AgentStartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan GracefulCloseTimeout = TimeSpan.FromSeconds(8);

    public static bool CanInstall(UnityProcessInfo target, out string reason) =>
        CanInstall(target.ExecutablePath, target.Backend, out reason);

    public static bool CanInstall(string executablePath, string backend, out string reason)
    {
        reason = string.Empty;
        if (backend is not ("Mono" or "IL2CPP"))
        {
            reason = "Unity backend could not be identified.";
            return false;
        }

        var gameDirectory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            reason = "Game directory could not be resolved.";
            return false;
        }

        if (!Directory.Exists(Path.Combine(gameDirectory, "BepInEx")))
        {
            reason = "BepInEx is not installed in this game. Install the matching BepInEx 6 runtime first.";
            return false;
        }

        var agentPath = GetAgentPayloadPath(backend);
        if (!File.Exists(agentPath))
        {
            reason = $"Viewer does not contain the {backend} Agent payload. Build that backend before using GUI install.";
            return false;
        }

        var protocolPath = Path.Combine(AppContext.BaseDirectory, "U3DViewer.Protocol.dll");
        var nativePath = Path.Combine(AppContext.BaseDirectory, "U3DViewer.NativeBridge.dll");
        if (!File.Exists(protocolPath) || !File.Exists(nativePath))
        {
            reason = "Viewer payload is incomplete: Protocol or NativeBridge is missing.";
            return false;
        }

        return true;
    }

    public static async Task<GameAutomationResult> InstallAndRestartAsync(
        UnityProcessInfo target,
        CancellationToken cancellationToken = default)
    {
        var deploy = Deploy(target.ExecutablePath, target.Backend);
        if (!deploy.Success)
        {
            return deploy;
        }

        Process? process = null;
        try
        {
            process = Process.GetProcessById(target.ProcessId);
            if (!process.HasExited)
            {
                if (!process.CloseMainWindow())
                {
                    return new GameAutomationResult(
                        false,
                        "Agent files were installed, but the game did not accept a graceful close request. Close it manually, then use Open Game… to launch it with the Agent.");
                }

                var exitTask = process.WaitForExitAsync(cancellationToken);
                var completed = await Task.WhenAny(exitTask, Task.Delay(GracefulCloseTimeout, cancellationToken));
                if (completed != exitTask)
                {
                    return new GameAutomationResult(
                        false,
                        "Agent files were installed, but the game did not exit within 8 seconds. It was not force-killed; close it manually, then launch it again.");
                }

                await exitTask;
            }
        }
        catch (ArgumentException)
        {
            // Process already exited between scan and restart. Continue with relaunch.
        }
        finally
        {
            process?.Dispose();
        }

        return await StartAndWaitAsync(target.ExecutablePath, cancellationToken);
    }

    public static async Task<GameAutomationResult> InstallLaunchAndWaitAsync(
        string executablePath,
        CancellationToken cancellationToken = default)
    {
        if (!UnityProcessDiscovery.TryInspectExecutable(executablePath, out var backend))
        {
            return new GameAutomationResult(false, "The selected executable does not look like a Unity standalone game.");
        }

        var deploy = Deploy(executablePath, backend);
        if (!deploy.Success)
        {
            return deploy;
        }

        return await StartAndWaitAsync(executablePath, cancellationToken);
    }

    private static GameAutomationResult Deploy(string executablePath, string backend)
    {
        if (!CanInstall(executablePath, backend, out var reason))
        {
            return new GameAutomationResult(false, reason);
        }

        try
        {
            var gameDirectory = Path.GetDirectoryName(executablePath)!;
            var pluginDirectory = Path.Combine(gameDirectory, "BepInEx", "plugins", "U3DViewer");
            Directory.CreateDirectory(pluginDirectory);

            var agentPath = GetAgentPayloadPath(backend);
            var protocolPath = Path.Combine(AppContext.BaseDirectory, "U3DViewer.Protocol.dll");
            var nativePath = Path.Combine(AppContext.BaseDirectory, "U3DViewer.NativeBridge.dll");

            File.Copy(agentPath, Path.Combine(pluginDirectory, Path.GetFileName(agentPath)), overwrite: true);
            File.Copy(protocolPath, Path.Combine(pluginDirectory, "U3DViewer.Protocol.dll"), overwrite: true);
            File.Copy(nativePath, Path.Combine(gameDirectory, "U3DViewer.NativeBridge.dll"), overwrite: true);

            return new GameAutomationResult(true, $"Installed {backend} Agent into {pluginDirectory}.");
        }
        catch (UnauthorizedAccessException ex)
        {
            return new GameAutomationResult(false, $"Deployment requires write access to the game directory: {ex.Message}");
        }
        catch (IOException ex)
        {
            return new GameAutomationResult(false, $"Deployment failed because a target file is locked or unavailable: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new GameAutomationResult(false, $"Deployment failed: {ex.Message}");
        }
    }

    private static async Task<GameAutomationResult> StartAndWaitAsync(
        string executablePath,
        CancellationToken cancellationToken)
    {
        var gameDirectory = Path.GetDirectoryName(executablePath)!;
        Process? process;

        try
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = gameDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            return new GameAutomationResult(false, $"Game launch failed: {ex.Message}");
        }

        if (process is null)
        {
            return new GameAutomationResult(false, "Windows did not return a process for the launched game.");
        }

        using (process)
        {
            var deadline = DateTime.UtcNow + AgentStartupTimeout;
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (process.HasExited)
                {
                    return new GameAutomationResult(false, $"Game exited before the U3DViewer Agent became ready (exit code {process.ExitCode}).");
                }

                var target = UnityProcessDiscovery.Scan()
                    .FirstOrDefault(item => item.ProcessId == process.Id);

                if (target?.AgentStatus == AgentProcessStatus.Ready)
                {
                    return new GameAutomationResult(true, "Agent is ready.", target);
                }

                await Task.Delay(500, cancellationToken);
            }

            return new GameAutomationResult(
                false,
                "Game started, but the Agent did not become ready within 30 seconds. Check BepInEx/LogOutput.log for plugin load errors.");
        }
    }

    private static string GetAgentPayloadPath(string backend)
    {
        var fileName = backend == "Mono"
            ? "U3DViewer.Agent.Mono.dll"
            : "U3DViewer.Agent.IL2CPP.dll";

        return Path.Combine(AppContext.BaseDirectory, "payload", backend, fileName);
    }
}
