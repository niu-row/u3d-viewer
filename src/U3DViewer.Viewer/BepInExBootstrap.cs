using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;

namespace U3DViewer.Viewer;

internal sealed record BepInExBootstrapResult(bool Success, string Message);

internal static class BepInExBootstrap
{
    private const string MonoArchiveUrl = "https://builds.bepinex.dev/projects/bepinex_be/785/BepInEx-Unity.Mono-win-x64-6.0.0-be.785%2B6abdba4.zip";
    private const string Il2CppArchiveUrl = "https://builds.bepinex.dev/projects/bepinex_be/785/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.785%2B6abdba4.zip";
    private static readonly TimeSpan InteropTimeout = TimeSpan.FromMinutes(2);
    private static readonly HttpClient Http = CreateHttpClient();

    public static bool IsInstalled(string executablePath)
    {
        var gameDirectory = Path.GetDirectoryName(executablePath);
        return !string.IsNullOrWhiteSpace(gameDirectory) &&
               File.Exists(Path.Combine(gameDirectory, "BepInEx", "core", "BepInEx.Core.dll"));
    }

    public static async Task<BepInExBootstrapResult> EnsureInstalledAsync(
        string executablePath,
        string backend,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (IsInstalled(executablePath))
        {
            return new BepInExBootstrapResult(true, "BepInEx is already installed.");
        }

        if (backend is not ("Mono" or "IL2CPP"))
        {
            return new BepInExBootstrapResult(false, $"Cannot select a BepInEx package for backend '{backend}'.");
        }

        var gameDirectory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return new BepInExBootstrapResult(false, "Game directory could not be resolved.");
        }

        try
        {
            var cacheDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "U3DViewer",
                "Downloads");
            Directory.CreateDirectory(cacheDirectory);

            var archiveName = backend == "Mono"
                ? "BepInEx-Unity.Mono-win-x64-6.0.0-be.785.zip"
                : "BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.785.zip";
            var archivePath = Path.Combine(cacheDirectory, archiveName);
            var url = backend == "Mono" ? MonoArchiveUrl : Il2CppArchiveUrl;

            if (!File.Exists(archivePath))
            {
                progress?.Report($"Downloading BepInEx 6 for Unity {backend} x64...");
                using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = File.Create(archivePath);
                await input.CopyToAsync(output, cancellationToken);
            }

            progress?.Report("Installing BepInEx into the selected game...");
            ZipFile.ExtractToDirectory(archivePath, gameDirectory, overwriteFiles: true);

            if (!IsInstalled(executablePath))
            {
                return new BepInExBootstrapResult(false, "BepInEx archive was extracted, but BepInEx.Core.dll was not found afterwards.");
            }

            return new BepInExBootstrapResult(true, "BepInEx installed.");
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

        var interopDirectory = Path.Combine(gameDirectory, "BepInEx", "interop");
        var generationMarker = Path.Combine(interopDirectory, "assembly-hash.txt");

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
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (AgentBuilder.TryResolveUnityReferences(executablePath, "IL2CPP", out _, out _))
                {
                    progress?.Report("IL2CPP interop assemblies generated. Restarting into U3DViewer mode...");
                    await StopBootstrapProcessAsync(process, cancellationToken);
                    return new BepInExBootstrapResult(true, "IL2CPP interop assemblies generated.");
                }

                if (File.Exists(generationMarker))
                {
                    AgentBuilder.TryResolveUnityReferences(
                        executablePath,
                        "IL2CPP",
                        out _,
                        out var resolutionError);

                    await StopBootstrapProcessAsync(process, cancellationToken);
                    return new BepInExBootstrapResult(
                        false,
                        "BepInEx finished IL2CPP interop generation, but U3DViewer could not resolve the required Unity runtime types. " +
                        resolutionError);
                }

                if (process.HasExited)
                {
                    return new BepInExBootstrapResult(
                        false,
                        $"Game exited before IL2CPP interop generation completed (exit code {process.ExitCode}). Check BepInEx/LogOutput.log.");
                }

                await Task.Delay(500, cancellationToken);
            }

            await StopBootstrapProcessAsync(process, cancellationToken);
            return new BepInExBootstrapResult(
                false,
                "BepInEx did not finish IL2CPP interop generation within 2 minutes. Check BepInEx/LogOutput.log.");
        }
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
