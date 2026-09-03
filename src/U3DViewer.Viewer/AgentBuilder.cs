using System.Diagnostics;

namespace U3DViewer.Viewer;

internal sealed record AgentBuildResult(
    bool Success,
    string Message,
    string? AgentPath = null,
    string? ProtocolPath = null);

internal static class AgentBuilder
{
    private const string Configuration = "Release";
    private static readonly string[] RequiredUnityAssemblies =
    {
        "UnityEngine.CoreModule.dll",
        "UnityEngine.SceneManagementModule.dll"
    };

    public static bool CanBuild(out string reason)
    {
        var sourceRoot = Path.Combine(AppContext.BaseDirectory, "agent-builder");
        if (!Directory.Exists(sourceRoot))
        {
            reason = "Viewer Agent Builder payload is missing. Rebuild U3DViewer.Viewer.";
            return false;
        }

        if (ResolveDotNet() is null)
        {
            reason = ".NET SDK was not found. Install the .NET 8 SDK, then reopen U3DViewer.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public static bool HasRequiredReferences(string executablePath, string backend, out string referenceDirectory)
    {
        referenceDirectory = GetReferenceDirectory(executablePath, backend);
        return RequiredUnityAssemblies.All(file => File.Exists(Path.Combine(referenceDirectory, file)));
    }

    public static async Task<AgentBuildResult> BuildAsync(
        string executablePath,
        string backend,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!CanBuild(out var reason))
        {
            return new AgentBuildResult(false, reason);
        }

        if (backend is not ("Mono" or "IL2CPP"))
        {
            return new AgentBuildResult(false, $"Unsupported Unity backend: {backend}.");
        }

        if (!HasRequiredReferences(executablePath, backend, out var referenceDirectory))
        {
            return new AgentBuildResult(
                false,
                backend == "IL2CPP"
                    ? "IL2CPP interop assemblies are not ready yet."
                    : $"Unity managed reference assemblies are missing from {referenceDirectory}.");
        }

        var dotnet = ResolveDotNet()!;
        var sourceRoot = Path.Combine(AppContext.BaseDirectory, "agent-builder");
        var workspace = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "U3DViewer",
            "AgentBuilder",
            backend);

        try
        {
            progress?.Report($"Preparing {backend} Agent build workspace...");
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
            CopyDirectory(sourceRoot, workspace);
        }
        catch (Exception ex)
        {
            return new AgentBuildResult(false, $"Could not prepare Agent Builder workspace: {ex.Message}");
        }

        var projectFolder = backend == "Mono"
            ? "U3DViewer.Agent.Mono"
            : "U3DViewer.Agent.IL2CPP";
        var targetFramework = backend == "Mono" ? "netstandard2.0" : "net6.0";
        var projectPath = Path.Combine(workspace, "src", projectFolder, projectFolder + ".csproj");

        progress?.Report($"Building {backend} Agent against the selected game's Unity assemblies...");

        var startInfo = new ProcessStartInfo
        {
            FileName = dotnet,
            WorkingDirectory = workspace,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(Configuration);
        startInfo.ArgumentList.Add("--nologo");
        startInfo.ArgumentList.Add($"-p:UnityReferenceDir={referenceDirectory}");

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new AgentBuildResult(false, "dotnet build could not be started.");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                var detail = Tail(string.Join(Environment.NewLine, stdout, stderr), 24);
                return new AgentBuildResult(false, $"{backend} Agent build failed.{Environment.NewLine}{detail}");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AgentBuildResult(false, $"{backend} Agent build failed to start: {ex.Message}");
        }

        var outputDirectory = Path.Combine(workspace, "src", projectFolder, "bin", Configuration, targetFramework);
        var agentPath = Path.Combine(outputDirectory, projectFolder + ".dll");
        var protocolPath = Path.Combine(outputDirectory, "U3DViewer.Protocol.dll");

        if (!File.Exists(agentPath) || !File.Exists(protocolPath))
        {
            return new AgentBuildResult(false, "Agent build completed but expected output DLLs were not found.");
        }

        return new AgentBuildResult(true, $"{backend} Agent built successfully.", agentPath, protocolPath);
    }

    private static string GetReferenceDirectory(string executablePath, string backend)
    {
        var gameDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty;
        if (backend == "IL2CPP")
        {
            return Path.Combine(gameDirectory, "BepInEx", "interop");
        }

        var executableName = Path.GetFileNameWithoutExtension(executablePath);
        return Path.Combine(gameDirectory, executableName + "_Data", "Managed");
    }

    private static string? ResolveDotNet()
    {
        var candidates = new List<string>();
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(dotnetRoot))
        {
            candidates.Add(Path.Combine(dotnetRoot, "dotnet.exe"));
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            candidates.Add(Path.Combine(programFiles, "dotnet", "dotnet.exe"));
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                candidates.Add(Path.Combine(directory.Trim(), "dotnet.exe"));
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static string Tail(string text, int maxLines)
    {
        var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(Environment.NewLine, lines.Skip(Math.Max(0, lines.Length - maxLines)));
    }
}
