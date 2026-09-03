using System.Diagnostics;

namespace U3DViewer.Viewer;

internal sealed record GameAutomationResult(bool Success, string Message, UnityProcessInfo? Target = null);

internal static class GameAutomation
{
    private static readonly TimeSpan AgentStartupTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan GracefulCloseTimeout = TimeSpan.FromSeconds(10);

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

        if (!File.Exists(executablePath))
        {
            reason = "Game executable no longer exists.";
            return false;
        }

        if (!AgentBuilder.CanBuild(out reason))
        {
            return false;
        }

        var nativePath = Path.Combine(AppContext.BaseDirectory, "U3DViewer.NativeBridge.dll");
        if (!File.Exists(nativePath))
        {
            reason = "U3DViewer.NativeBridge.dll is missing next to the Viewer executable.";
            return false;
        }

        return true;
    }

    public static async Task<GameAutomationResult> InstallAndRestartAsync(
        UnityProcessInfo target,
        IProgress<string>? progress,
        CancellationToken cancellationToken = default)
    {
        progress?.Report($"Closing {target.ProcessName} so U3DViewer can prepare the runtime...");
        var close = await CloseExistingProcessAsync(target.ProcessId, cancellationToken);
        if (!close.Success)
        {
            return close;
        }

        return await PrepareLaunchAndWaitAsync(target.ExecutablePath, target.Backend, progress, cancellationToken);
    }

    public static async Task<GameAutomationResult> InstallLaunchAndWaitAsync(
        string executablePath,
        IProgress<string>? progress,
        CancellationToken cancellationToken = default)
    {
        if (!UnityProcessDiscovery.TryInspectExecutable(executablePath, out var backend))
        {
            return new GameAutomationResult(false, "The selected executable does not look like a Unity standalone game.");
        }

        return await PrepareLaunchAndWaitAsync(executablePath, backend, progress, cancellationToken);
    }

    private static async Task<GameAutomationResult> PrepareLaunchAndWaitAsync(
        string executablePath,
        string backend,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!CanInstall(executablePath, backend, out var reason))
        {
            return new GameAutomationResult(false, reason);
        }

        progress?.Report($"Preparing Unity {backend} runtime...");
        var bepinex = await BepInExBootstrap.EnsureInstalledAsync(executablePath, backend, progress, cancellationToken);
        if (!bepinex.Success)
        {
            return new GameAutomationResult(false, bepinex.Message);
        }

        if (backend == "IL2CPP")
        {
            var interop = await BepInExBootstrap.EnsureIl2CppInteropAsync(executablePath, progress, cancellationToken);
            if (!interop.Success)
            {
                return new GameAutomationResult(false, interop.Message);
            }
        }

        var build = await AgentBuilder.BuildAsync(executablePath, backend, progress, cancellationToken);
        if (!build.Success || string.IsNullOrWhiteSpace(build.AgentPath) || string.IsNullOrWhiteSpace(build.ProtocolPath))
        {
            return new GameAutomationResult(false, build.Message);
        }

        progress?.Report("Deploying U3DViewer Agent into the selected game...");
        var deploy = Deploy(executablePath, backend, build.AgentPath, build.ProtocolPath);
        if (!deploy.Success)
        {
            return deploy;
        }

        progress?.Report("Launching game and waiting for U3DViewer Agent...");
        return await StartAndWaitAsync(executablePath, cancellationToken);
    }

    private static GameAutomationResult Deploy(
        string executablePath,
        string backend,
        string agentPath,
        string protocolPath)
    {
        try
        {
            var gameDirectory = Path.GetDirectoryName(executablePath)!;
            var pluginDirectory = Path.Combine(gameDirectory, "BepInEx", "plugins", "U3DViewer");
            Directory.CreateDirectory(pluginDirectory);

            foreach (var staleAgent in new[] { "U3DViewer.Agent.Mono.dll", "U3DViewer.Agent.IL2CPP.dll" })
            {
                var stalePath = Path.Combine(pluginDirectory, staleAgent);
                if (File.Exists(stalePath))
                {
                    File.Delete(stalePath);
                }
            }

            File.Copy(agentPath, Path.Combine(pluginDirectory, Path.GetFileName(agentPath)), overwrite: true);
            File.Copy(protocolPath, Path.Combine(pluginDirectory, "U3DViewer.Protocol.dll"), overwrite: true);
            File.Copy(
                Path.Combine(AppContext.BaseDirectory, "U3DViewer.NativeBridge.dll"),
                Path.Combine(gameDirectory, "U3DViewer.NativeBridge.dll"),
                overwrite: true);

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

    private static async Task<GameAutomationResult> CloseExistingProcessAsync(
        int processId,
        CancellationToken cancellationToken)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return new GameAutomationResult(true, "Process already exited.");
            }

            if (!process.CloseMainWindow())
            {
                return new GameAutomationResult(
                    false,
                    "The running game did not accept a normal close request. Close it manually, then use Open Game…; U3DViewer will not force-kill a game you were already running.");
            }

            var waitTask = process.WaitForExitAsync(cancellationToken);
            var completed = await Task.WhenAny(waitTask, Task.Delay(GracefulCloseTimeout, cancellationToken));
            if (completed != waitTask)
            {
                return new GameAutomationResult(
                    false,
                    "The running game did not exit within 10 seconds. Close it manually, then use Open Game…; U3DViewer will not force-kill an existing session.");
            }

            await waitTask;
            return new GameAutomationResult(true, "Game closed.");
        }
        catch (ArgumentException)
        {
            return new GameAutomationResult(true, "Process already exited.");
        }
        finally
        {
            process?.Dispose();
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

                var target = UnityProcessDiscovery.Scan().FirstOrDefault(item =>
                    item.ProcessId == process.Id ||
                    string.Equals(item.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase));

                if (target?.AgentStatus == AgentProcessStatus.Ready)
                {
                    return new GameAutomationResult(true, "Agent is ready.", target);
                }

                if (process.HasExited && target is null)
                {
                    return new GameAutomationResult(false, $"Game exited before the U3DViewer Agent became ready (exit code {process.ExitCode}). Check BepInEx/LogOutput.log.");
                }

                await Task.Delay(500, cancellationToken);
            }

            return new GameAutomationResult(
                false,
                "Game started, but the Agent did not become ready within 60 seconds. Check BepInEx/LogOutput.log for plugin load errors.");
        }
    }
}
