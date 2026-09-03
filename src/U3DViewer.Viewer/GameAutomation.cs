using System.Diagnostics;

namespace U3DViewer.Viewer;

internal sealed record GameAutomationResult(bool Success, string Message, UnityProcessInfo? Target = null);

internal static class GameAutomation
{
    private static readonly TimeSpan AgentStartupTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan BepInExStartupProbeTimeout = TimeSpan.FromSeconds(8);
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

        if (!UnityProcessDiscovery.TryGetExecutableArchitecture(executablePath, out var architecture))
        {
            reason = "The target game PE architecture could not be determined.";
            return false;
        }

        if (architecture is not ("x86" or "x64"))
        {
            reason = $"Unsupported target game architecture: {architecture}.";
            return false;
        }

        if (!AgentBuilder.CanBuild(executablePath, backend, out reason))
        {
            return false;
        }

        var nativePath = GetNativeBridgeSourcePath(architecture);
        if (!File.Exists(nativePath))
        {
            reason = architecture == "x86"
                ? "U3DViewer.NativeBridge.x86.dll is missing next to the Viewer executable. Rebuild U3DViewer so the Win32 NativeBridge is produced."
                : "U3DViewer.NativeBridge.dll is missing next to the Viewer executable.";
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
        if (!UnityProcessDiscovery.TryInspectExecutable(executablePath, out var backend, out _))
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

        UnityProcessDiscovery.TryGetExecutableArchitecture(executablePath, out var architecture);
        progress?.Report($"Preparing Unity {backend} {architecture} runtime...");
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

        progress?.Report($"Deploying U3DViewer Agent and {architecture} NativeBridge into the selected game...");
        var deploy = Deploy(executablePath, backend, architecture, build.AgentPath, build.ProtocolPath);
        if (!deploy.Success)
        {
            return deploy;
        }

        progress?.Report("Launching game and waiting for U3DViewer Agent...");
        return await StartAndWaitAsync(executablePath, backend, progress, cancellationToken);
    }

    private static GameAutomationResult Deploy(
        string executablePath,
        string backend,
        string architecture,
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
                GetNativeBridgeSourcePath(architecture),
                Path.Combine(gameDirectory, "U3DViewer.NativeBridge.dll"),
                overwrite: true);

            return new GameAutomationResult(
                true,
                $"Installed {backend} Agent and {architecture} NativeBridge into {pluginDirectory}.");
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

    private static string GetNativeBridgeSourcePath(string architecture) =>
        Path.Combine(
            AppContext.BaseDirectory,
            architecture == "x86" ? "U3DViewer.NativeBridge.x86.dll" : "U3DViewer.NativeBridge.dll");

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
        string backend,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var gameDirectory = Path.GetDirectoryName(executablePath)!;
        var legacyProxyAttempted = false;

        while (true)
        {
            var process = StartGame(executablePath, gameDirectory, out var launchError);
            if (process is null)
            {
                return new GameAutomationResult(false, launchError);
            }

            using (process)
            {
                var logDescription = DescribeBepInExLog(gameDirectory);
                progress?.Report(
                    $"Game process started (PID {process.Id}). Waiting for Agent pipe. BepInEx log: {logDescription}");

                var startedAt = DateTime.UtcNow;
                var deadline = startedAt + AgentStartupTimeout;
                var retryWithLegacyProxy = false;

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
                        return new GameAutomationResult(
                            false,
                            $"Game exited before the U3DViewer Agent became ready (exit code {process.ExitCode}). " +
                            $"BepInEx diagnostics: {DescribeBepInExLog(gameDirectory)}");
                    }

                    if (backend == "Mono")
                    {
                        var preloaderFailure = FindLatestPreloaderFailureLog(gameDirectory, startedAt);
                        if (preloaderFailure is not null)
                        {
                            return new GameAutomationResult(
                                false,
                                $"BepInEx Doorstop loaded, but the Mono preloader failed before the Agent became ready. Preloader log: {preloaderFailure}");
                        }
                    }

                    if (backend == "Mono" &&
                        !legacyProxyAttempted &&
                        DateTime.UtcNow - startedAt >= BepInExStartupProbeTimeout &&
                        !HasBepInExLog(gameDirectory))
                    {
                        legacyProxyAttempted = true;
                        progress?.Report(
                            "BepInEx did not create a log within 8 seconds. Doorstop may not be loading through winhttp.dll on this older Unity game. Trying the legacy version.dll proxy...");

                        await StopLaunchedProcessAsync(process, cancellationToken);
                        if (!BepInExBootstrap.TryEnableLegacyMonoDoorstopProxy(executablePath, out var proxyMessage))
                        {
                            return new GameAutomationResult(
                                false,
                                $"BepInEx did not initialize and the legacy Doorstop fallback could not be applied. {proxyMessage}");
                        }

                        progress?.Report(proxyMessage + " Relaunching the game...");
                        retryWithLegacyProxy = true;
                        break;
                    }

                    await Task.Delay(500, cancellationToken);
                }

                if (retryWithLegacyProxy)
                {
                    continue;
                }

                return new GameAutomationResult(
                    false,
                    $"Game started, but the Agent did not become ready within 60 seconds. " +
                    $"BepInEx diagnostics: {DescribeBepInExLog(gameDirectory)}");
            }
        }
    }

    private static Process? StartGame(string executablePath, string gameDirectory, out string error)
    {
        error = string.Empty;
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = gameDirectory,
                UseShellExecute = true
            });
            if (process is null)
            {
                error = "Windows did not return a process for the launched game.";
            }
            return process;
        }
        catch (Exception ex)
        {
            error = $"Game launch failed: {ex.Message}";
            return null;
        }
    }

    private static bool HasBepInExLog(string gameDirectory) =>
        File.Exists(Path.Combine(gameDirectory, "BepInEx", "LogOutput.log")) ||
        File.Exists(Path.Combine(gameDirectory, "BepInEx", "LogOutput.txt"));

    private static string? FindLatestPreloaderFailureLog(string gameDirectory, DateTime notBeforeUtc)
    {
        try
        {
            return Directory.EnumerateFiles(gameDirectory, "preloader_*.log", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .Where(info => info.LastWriteTimeUtc >= notBeforeUtc.AddSeconds(-1))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .Select(info => info.FullName)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string DescribeBepInExLog(string gameDirectory)
    {
        var logPath = Path.Combine(gameDirectory, "BepInEx", "LogOutput.log");
        if (File.Exists(logPath))
        {
            return logPath;
        }

        var textPath = Path.Combine(gameDirectory, "BepInEx", "LogOutput.txt");
        if (File.Exists(textPath))
        {
            return textPath;
        }

        try
        {
            var preloaderLog = Directory.EnumerateFiles(gameDirectory, "preloader_*.log", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .Select(info => info.FullName)
                .FirstOrDefault();
            if (preloaderLog is not null)
            {
                return $"preloader failure log: {preloaderLog}";
            }
        }
        catch
        {
        }

        return $"no LogOutput.log/.txt created yet under {Path.Combine(gameDirectory, "BepInEx")}";
    }

    private static async Task StopLaunchedProcessAsync(Process process, CancellationToken cancellationToken)
    {
        if (process.HasExited)
        {
            return;
        }

        try
        {
            if (process.CloseMainWindow())
            {
                var waitTask = process.WaitForExitAsync(cancellationToken);
                var completed = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));
                if (completed == waitTask)
                {
                    await waitTask;
                    return;
                }
            }

            if (!process.HasExited)
            {
                // This process was launched by U3DViewer specifically for this preparation attempt.
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken);
            }
        }
        catch
        {
            // The following proxy switch/relaunch will report a useful error if the process still holds the DLL.
        }
    }
}
