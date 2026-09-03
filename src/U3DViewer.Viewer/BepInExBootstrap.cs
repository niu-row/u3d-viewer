using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text;

namespace U3DViewer.Viewer;

internal sealed record BepInExBootstrapResult(bool Success, string Message);

internal static class BepInExBootstrap
{
    private const string MonoArchiveUrlX64 = "https://builds.bepinex.dev/projects/bepinex_be/785/BepInEx-Unity.Mono-win-x64-6.0.0-be.785%2B6abdba4.zip";
    private const string MonoArchiveUrlX86 = "https://builds.bepinex.dev/projects/bepinex_be/785/BepInEx-Unity.Mono-win-x86-6.0.0-be.785%2B6abdba4.zip";
    private const string Il2CppArchiveUrlX64 = "https://builds.bepinex.dev/projects/bepinex_be/785/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.785%2B6abdba4.zip";
    private const string Il2CppArchiveUrlX86 = "https://builds.bepinex.dev/projects/bepinex_be/785/BepInEx-Unity.IL2CPP-win-x86-6.0.0-be.785%2B6abdba4.zip";
    private static readonly TimeSpan InteropTimeout = TimeSpan.FromMinutes(2);
    private static readonly HttpClient Http = CreateHttpClient();

    public static bool IsInstalled(string executablePath, string? backend = null)
    {
        var gameDirectory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(gameDirectory) ||
            !UnityProcessDiscovery.TryGetExecutableArchitecture(executablePath, out var architecture) ||
            architecture is not ("x86" or "x64"))
        {
            return false;
        }

        var coreDirectory = Path.Combine(gameDirectory, "BepInEx", "core");
        if (!File.Exists(Path.Combine(coreDirectory, "BepInEx.Core.dll")) ||
            !File.Exists(Path.Combine(gameDirectory, "doorstop_config.ini")) ||
            !HasMatchingDoorstopProxy(gameDirectory, architecture))
        {
            return false;
        }

        return backend switch
        {
            "Mono" => File.Exists(Path.Combine(coreDirectory, "BepInEx.Unity.Mono.Preloader.dll")),
            "IL2CPP" => File.Exists(Path.Combine(coreDirectory, "BepInEx.Unity.IL2CPP.dll")),
            _ => true
        };
    }

    public static async Task<BepInExBootstrapResult> EnsureInstalledAsync(
        string executablePath,
        string backend,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (backend is not ("Mono" or "IL2CPP"))
        {
            return new BepInExBootstrapResult(false, $"Cannot select a BepInEx package for backend '{backend}'.");
        }

        if (!UnityProcessDiscovery.TryGetExecutableArchitecture(executablePath, out var architecture))
        {
            return new BepInExBootstrapResult(false, "Could not determine the target game's PE architecture.");
        }

        if (architecture is not ("x86" or "x64"))
        {
            return new BepInExBootstrapResult(false, $"Unsupported target game architecture: {architecture}.");
        }

        if (IsInstalled(executablePath, backend))
        {
            progress?.Report($"Detected Unity {backend} {architecture}; existing BepInEx loader matches the game architecture.");
            return ApplyLowOverheadProfile(executablePath, "BepInEx is already installed.", progress);
        }

        var gameDirectory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return new BepInExBootstrapResult(false, "Game directory could not be resolved.");
        }

        try
        {
            var cacheDirectory = ViewerPaths.GetGameDownloadsDirectory(executablePath);
            Directory.CreateDirectory(cacheDirectory);

            var archiveName = $"BepInEx-Unity.{(backend == "Mono" ? "Mono" : "IL2CPP")}-win-{architecture}-6.0.0-be.785.zip";
            var archivePath = Path.Combine(cacheDirectory, archiveName);
            var url = GetArchiveUrl(backend, architecture);

            if (!File.Exists(archivePath))
            {
                progress?.Report($"Downloading BepInEx 6 for Unity {backend} {architecture}...");
                using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = File.Create(archivePath);
                await input.CopyToAsync(output, cancellationToken);
            }

            progress?.Report($"Installing or repairing BepInEx {architecture} in the selected game...");
            ZipFile.ExtractToDirectory(archivePath, gameDirectory, overwriteFiles: true);

            if (!IsInstalled(executablePath, backend))
            {
                return new BepInExBootstrapResult(
                    false,
                    $"BepInEx {architecture} archive was extracted, but the Doorstop loader or backend preloader files are still incomplete or architecture-mismatched.");
            }

            return ApplyLowOverheadProfile(executablePath, $"BepInEx {architecture} installed/repaired.", progress);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new BepInExBootstrapResult(false, $"Installing BepInEx requires write access to the game directory: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            return new BepInExBootstrapResult(false, $"BepInEx download failed: {ex.Message}");
        }
        catch (InvalidDataException ex)
        {
            return new BepInExBootstrapResult(false, $"Downloaded BepInEx archive is invalid: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new BepInExBootstrapResult(false, $"BepInEx installation failed: {ex.Message}");
        }
    }

    public static bool TryEnableLegacyMonoDoorstopProxy(string executablePath, out string message)
    {
        message = string.Empty;
        var gameDirectory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            message = "Game directory could not be resolved while switching the Doorstop proxy.";
            return false;
        }

        if (!UnityProcessDiscovery.TryGetExecutableArchitecture(executablePath, out var architecture))
        {
            message = "Could not determine target architecture while switching the Doorstop proxy.";
            return false;
        }

        var winHttpPath = Path.Combine(gameDirectory, "winhttp.dll");
        var versionPath = Path.Combine(gameDirectory, "version.dll");

        try
        {
            if (File.Exists(versionPath) && !File.Exists(winHttpPath))
            {
                if (ProxyMatchesArchitecture(versionPath, architecture))
                {
                    message = $"Legacy Doorstop version.dll proxy is already enabled for {architecture}.";
                    return true;
                }

                message = $"Existing version.dll does not match the target {architecture} architecture and no matching winhttp.dll is available to replace it.";
                return false;
            }

            if (!File.Exists(winHttpPath))
            {
                message = $"BepInEx Doorstop proxy was not found: {winHttpPath}";
                return false;
            }

            if (!ProxyMatchesArchitecture(winHttpPath, architecture))
            {
                message = $"BepInEx winhttp.dll does not match the target {architecture} architecture. Re-run runtime preparation to repair BepInEx.";
                return false;
            }

            if (File.Exists(versionPath))
            {
                if (ProxyMatchesArchitecture(versionPath, architecture))
                {
                    message =
                        $"Cannot automatically switch BepInEx to version.dll because the game already has an architecture-compatible file: {versionPath}";
                    return false;
                }

                // A DLL with the opposite PE architecture cannot be a usable proxy for this target EXE.
                // This also repairs stale proxies left by an earlier U3DViewer run that installed the wrong BepInEx architecture.
                File.Delete(versionPath);
            }

            File.Move(winHttpPath, versionPath);
            message =
                $"Switched BepInEx Doorstop from winhttp.dll to version.dll for legacy Unity {architecture} compatibility.";
            return true;
        }
        catch (Exception ex)
        {
            message = $"Could not switch BepInEx to the legacy version.dll proxy: {ex.Message}";
            return false;
        }
    }

    public static async Task<BepInExBootstrapResult> EnsureIl2CppInteropAsync(
        string executablePath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (AgentBuilder.TryResolveUnityReferences(executablePath, "IL2CPP", out _, out _))
        {
            return new BepInExBootstrapResult(true, "IL2CPP interop assemblies are already available.");
        }

        var gameDirectory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return new BepInExBootstrapResult(false, "Game directory could not be resolved.");
        }

        progress?.Report("Starting the game once so BepInEx can generate IL2CPP interop assemblies...");

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
            return new BepInExBootstrapResult(false, $"Interop bootstrap launch failed: {ex.Message}");
        }

        if (process is null)
        {
            return new BepInExBootstrapResult(false, "Windows did not return a process for the IL2CPP bootstrap launch.");
        }

        using (process)
        {
            var deadline = DateTime.UtcNow + InteropTimeout;
            string resolutionError = "Required IL2CPP interop assemblies are not available yet.";

            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (AgentBuilder.TryResolveUnityReferences(
                        executablePath,
                        "IL2CPP",
                        out _,
                        out resolutionError))
                {
                    progress?.Report("IL2CPP interop assemblies generated. Restarting into U3DViewer mode...");
                    await StopBootstrapProcessAsync(process, cancellationToken);
                    return new BepInExBootstrapResult(true, "IL2CPP interop assemblies generated.");
                }

                if (process.HasExited)
                {
                    return new BepInExBootstrapResult(
                        false,
                        $"Game exited before IL2CPP interop generation completed (exit code {process.ExitCode}). " +
                        $"Last compatibility check: {resolutionError} Check BepInEx/LogOutput.log or LogOutput.txt.");
                }

                await Task.Delay(500, cancellationToken);
            }

            await StopBootstrapProcessAsync(process, cancellationToken);
            return new BepInExBootstrapResult(
                false,
                "BepInEx did not finish IL2CPP interop generation within 2 minutes. " +
                $"Last compatibility check: {resolutionError} Check BepInEx/LogOutput.log or LogOutput.txt.");
        }
    }

    private static string GetArchiveUrl(string backend, string architecture) =>
        (backend, architecture) switch
        {
            ("Mono", "x86") => MonoArchiveUrlX86,
            ("Mono", "x64") => MonoArchiveUrlX64,
            ("IL2CPP", "x86") => Il2CppArchiveUrlX86,
            ("IL2CPP", "x64") => Il2CppArchiveUrlX64,
            _ => throw new InvalidOperationException($"Unsupported BepInEx package combination: {backend}/{architecture}.")
        };

    private static bool HasMatchingDoorstopProxy(string gameDirectory, string architecture) =>
        ProxyMatchesArchitecture(Path.Combine(gameDirectory, "winhttp.dll"), architecture) ||
        ProxyMatchesArchitecture(Path.Combine(gameDirectory, "version.dll"), architecture);

    private static bool ProxyMatchesArchitecture(string path, string architecture) =>
        File.Exists(path) &&
        UnityProcessDiscovery.TryGetExecutableArchitecture(path, out var proxyArchitecture) &&
        string.Equals(proxyArchitecture, architecture, StringComparison.OrdinalIgnoreCase);

    private static BepInExBootstrapResult ApplyLowOverheadProfile(
        string executablePath,
        string successMessage,
        IProgress<string>? progress)
    {
        var gameDirectory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return new BepInExBootstrapResult(false, "Game directory could not be resolved while configuring BepInEx.");
        }

        try
        {
            progress?.Report("Applying low-overhead BepInEx logging profile with diagnostic console...");
            var configDirectory = Path.Combine(gameDirectory, "BepInEx", "config");
            Directory.CreateDirectory(configDirectory);
            var configPath = Path.Combine(configDirectory, "BepInEx.cfg");
            var config = File.Exists(configPath) ? File.ReadAllText(configPath) : string.Empty;

            // Keep BepInEx/plugin diagnostics visible while avoiding the expensive full Unity log bridge.
            config = UpsertIniValue(config, "Logging", "UnityLogListening", "false");
            config = UpsertIniValue(config, "Logging.Console", "Enabled", "true");
            config = UpsertIniValue(config, "Logging.Disk", "Enabled", "true");
            config = UpsertIniValue(config, "Logging.Disk", "WriteUnityLog", "false");
            config = UpsertIniValue(config, "Logging.Disk", "InstantFlushing", "false");

            File.WriteAllText(configPath, config, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return new BepInExBootstrapResult(true, successMessage + " Low-overhead logging profile with diagnostic console applied.");
        }
        catch (Exception ex)
        {
            return new BepInExBootstrapResult(false, $"BepInEx is installed, but its low-overhead logging profile could not be applied: {ex.Message}");
        }
    }

    private static string UpsertIniValue(string text, string section, string key, string value)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Length == 0
            ? new List<string>()
            : normalized.Split('\n').ToList();
        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var header = $"[{section}]";
        var sectionIndex = lines.FindIndex(line => string.Equals(line.Trim(), header, StringComparison.OrdinalIgnoreCase));
        if (sectionIndex < 0)
        {
            if (lines.Count > 0)
            {
                lines.Add(string.Empty);
            }
            lines.Add(header);
            lines.Add($"{key} = {value}");
        }
        else
        {
            var sectionEnd = lines.Count;
            for (var index = sectionIndex + 1; index < lines.Count; index++)
            {
                var trimmed = lines[index].Trim();
                if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
                {
                    sectionEnd = index;
                    break;
                }
            }

            var keyIndex = -1;
            for (var index = sectionIndex + 1; index < sectionEnd; index++)
            {
                var trimmed = lines[index].Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith(';'))
                {
                    continue;
                }

                var equalsIndex = trimmed.IndexOf('=');
                if (equalsIndex > 0 && string.Equals(trimmed[..equalsIndex].Trim(), key, StringComparison.OrdinalIgnoreCase))
                {
                    keyIndex = index;
                    break;
                }
            }

            if (keyIndex >= 0)
            {
                lines[keyIndex] = $"{key} = {value}";
            }
            else
            {
                lines.Insert(sectionEnd, $"{key} = {value}");
            }
        }

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static async Task StopBootstrapProcessAsync(Process process, CancellationToken cancellationToken)
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
                var completed = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(8), cancellationToken));
                if (completed == waitTask)
                {
                    await waitTask;
                    return;
                }
            }

            // This process was launched by U3DViewer only to generate interop files,
            // so force-closing it after generation is safe for the bootstrap workflow.
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken);
            }
        }
        catch
        {
            // The final launch will detect whether a stale bootstrap process is still present.
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("U3DViewer/0.1");
        return client;
    }
}
