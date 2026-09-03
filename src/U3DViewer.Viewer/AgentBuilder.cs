using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace U3DViewer.Viewer;

internal sealed record AgentBuildResult(
    bool Success,
    string Message,
    string? AgentPath = null,
    string? ProtocolPath = null);

internal sealed record UnityReferenceSet(string CorePath, string ScenePath);

internal static class AgentBuilder
{
    private const string Configuration = "Release";

    public static bool CanBuild(out string reason)
    {
        var sourceRoot = GetSourceRoot();
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

    public static bool CanBuild(string executablePath, string backend, out string reason)
    {
        reason = string.Empty;
        if (backend is not ("Mono" or "IL2CPP"))
        {
            reason = $"Unsupported Unity backend: {backend}.";
            return false;
        }

        var sourceRoot = GetSourceRoot();
        if (!Directory.Exists(sourceRoot))
        {
            reason = "Viewer Agent Builder payload is missing. Rebuild U3DViewer.Viewer.";
            return false;
        }

        if (TryResolveUnityReferences(executablePath, backend, out var references, out _))
        {
            try
            {
                var fingerprint = ComputeCompatibilityFingerprint(sourceRoot, backend, references!);
                if (TryGetCachedAgent(backend, fingerprint, out _, out _))
                {
                    return true;
                }
            }
            catch
            {
                // If cache probing fails, fall through to checking whether a fresh build is possible.
            }
        }

        if (ResolveDotNet() is null)
        {
            reason = ".NET SDK was not found and no compatible cached Agent is available. Install the .NET 8 SDK, then reopen U3DViewer.";
            return false;
        }

        return true;
    }

    public static bool HasRequiredReferences(string executablePath, string backend, out string referenceDirectory)
    {
        referenceDirectory = GetReferenceDirectory(executablePath, backend);
        return TryResolveUnityReferences(executablePath, backend, out _, out _);
    }

    public static bool TryResolveUnityReferences(
        string executablePath,
        string backend,
        out UnityReferenceSet? references,
        out string error)
    {
        references = null;
        error = string.Empty;

        var referenceDirectory = GetReferenceDirectory(executablePath, backend);
        if (!Directory.Exists(referenceDirectory))
        {
            error = backend == "IL2CPP"
                ? $"IL2CPP interop directory does not exist yet: {referenceDirectory}"
                : $"Unity managed directory does not exist: {referenceDirectory}";
            return false;
        }

        string[] candidates;
        try
        {
            candidates = Directory
                .EnumerateFiles(referenceDirectory, "UnityEngine*.dll", SearchOption.TopDirectoryOnly)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            error = $"Could not enumerate Unity assemblies in {referenceDirectory}: {ex.Message}";
            return false;
        }

        if (candidates.Length == 0)
        {
            error = $"No UnityEngine assemblies were found in {referenceDirectory}.";
            return false;
        }

        var corePath = candidates.FirstOrDefault(path =>
            AssemblyDefinesType(path, "UnityEngine", "GameObject"));
        if (corePath is null)
        {
            error = $"Interop generation produced {candidates.Length} Unity assemblies, but none defines UnityEngine.GameObject.";
            return false;
        }

        var scenePath = candidates.FirstOrDefault(path =>
            AssemblyDefinesType(path, "UnityEngine.SceneManagement", "SceneManager"));
        if (scenePath is null)
        {
            error = $"Interop generation produced {candidates.Length} Unity assemblies, but none defines UnityEngine.SceneManagement.SceneManager.";
            return false;
        }

        references = new UnityReferenceSet(corePath, scenePath);
        return true;
    }

    public static async Task<AgentBuildResult> BuildAsync(
        string executablePath,
        string backend,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (backend is not ("Mono" or "IL2CPP"))
        {
            return new AgentBuildResult(false, $"Unsupported Unity backend: {backend}.");
        }

        var sourceRoot = GetSourceRoot();
        if (!Directory.Exists(sourceRoot))
        {
            return new AgentBuildResult(false, "Viewer Agent Builder payload is missing. Rebuild U3DViewer.Viewer.");
        }

        if (!TryResolveUnityReferences(executablePath, backend, out var references, out var referenceError))
        {
            return new AgentBuildResult(false, referenceError);
        }

        string fingerprint;
        try
        {
            progress?.Report($"Checking {backend} Agent compatibility cache...");
            fingerprint = ComputeCompatibilityFingerprint(sourceRoot, backend, references!);
        }
        catch (Exception ex)
        {
            return new AgentBuildResult(false, $"Could not fingerprint the selected game's Unity runtime: {ex.Message}");
        }

        if (TryGetCachedAgent(backend, fingerprint, out var cachedAgent, out var cachedProtocol))
        {
            progress?.Report($"Using cached {backend} Agent ({ShortFingerprint(fingerprint)}). No rebuild needed.");
            return new AgentBuildResult(
                true,
                $"Compatible {backend} Agent cache hit.",
                cachedAgent,
                cachedProtocol);
        }

        var dotnet = ResolveDotNet();
        if (dotnet is null)
        {
            return new AgentBuildResult(
                false,
                ".NET SDK was not found and this Unity compatibility profile has not been built before. Install the .NET 8 SDK for the first build.");
        }

        var workspace = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "U3DViewer",
            "AgentBuilder",
            "Work",
            $"{backend}-{Environment.ProcessId}");

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

        var projectFolder = GetProjectFolder(backend);
        var targetFramework = backend == "Mono" ? "netstandard2.0" : "net6.0";
        var projectPath = Path.Combine(workspace, "src", projectFolder, projectFolder + ".csproj");

        progress?.Report($"Building {backend} Agent for compatibility profile {ShortFingerprint(fingerprint)} (first use only)...");

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
        startInfo.ArgumentList.Add($"-p:UnityCoreReference={references!.CorePath}");
        startInfo.ArgumentList.Add($"-p:UnitySceneReference={references.ScenePath}");

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

        try
        {
            var cacheDirectory = GetCacheDirectory(backend, fingerprint);
            Directory.CreateDirectory(cacheDirectory);

            var cacheAgent = Path.Combine(cacheDirectory, Path.GetFileName(agentPath));
            var cacheProtocol = Path.Combine(cacheDirectory, "U3DViewer.Protocol.dll");
            File.Copy(agentPath, cacheAgent, overwrite: true);
            File.Copy(protocolPath, cacheProtocol, overwrite: true);
            File.WriteAllText(
                Path.Combine(cacheDirectory, "compatibility.txt"),
                $"backend={backend}{Environment.NewLine}" +
                $"fingerprint={fingerprint}{Environment.NewLine}" +
                $"core={references.CorePath}{Environment.NewLine}" +
                $"scene={references.ScenePath}{Environment.NewLine}" +
                $"builtUtc={DateTime.UtcNow:O}{Environment.NewLine}");

            progress?.Report($"Cached {backend} Agent profile {ShortFingerprint(fingerprint)} for future launches.");
            return new AgentBuildResult(
                true,
                $"{backend} Agent built and cached successfully.",
                cacheAgent,
                cacheProtocol);
        }
        catch (Exception ex)
        {
            progress?.Report($"{backend} Agent built successfully, but cache write failed: {ex.Message}");
            return new AgentBuildResult(
                true,
                $"{backend} Agent built successfully; cache could not be written.",
                agentPath,
                protocolPath);
        }
    }

    private static string ComputeCompatibilityFingerprint(
        string sourceRoot,
        string backend,
        UnityReferenceSet references)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendText(hash, "U3DViewer-Agent-Compatibility-v2");
        AppendText(hash, backend);

        var projectFolder = GetProjectFolder(backend);
        var builderInputs = new List<string>();
        AddFiles(builderInputs, Path.Combine(sourceRoot, "src", projectFolder));
        AddFiles(builderInputs, Path.Combine(sourceRoot, "src", "U3DViewer.Protocol"));

        foreach (var rootFile in new[] { "Directory.Build.props", "NuGet.Config" })
        {
            var path = Path.Combine(sourceRoot, rootFile);
            if (File.Exists(path))
            {
                builderInputs.Add(path);
            }
        }

        foreach (var file in builderInputs
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(path => Path.GetRelativePath(sourceRoot, path), StringComparer.OrdinalIgnoreCase))
        {
            AppendText(hash, "builder:" + Path.GetRelativePath(sourceRoot, file).Replace('\\', '/'));
            AppendFileHash(hash, file);
        }

        AppendReference(hash, "core", references.CorePath);
        AppendReference(hash, "scene", references.ScenePath);

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendReference(IncrementalHash hash, string role, string path)
    {
        AppendText(hash, $"runtime:{role}:{Path.GetFileName(path)}");
        AppendFileHash(hash, path);
    }

    private static bool AssemblyDefinesType(string path, string @namespace, string name)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata)
            {
                return false;
            }

            var metadata = peReader.GetMetadataReader();
            foreach (var handle in metadata.TypeDefinitions)
            {
                var definition = metadata.GetTypeDefinition(handle);
                if (metadata.StringComparer.Equals(definition.Namespace, @namespace) &&
                    metadata.StringComparer.Equals(definition.Name, name))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Ignore native/corrupt/temporarily incomplete files while interop generation is still running.
        }

        return false;
    }

    private static void AddFiles(List<string> files, string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        files.AddRange(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutputPath(path)));
    }

    private static bool IsBuildOutputPath(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment =>
            string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(segment, "lib", StringComparison.OrdinalIgnoreCase));
    }

    private static void AppendText(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData(new byte[] { 0 });
    }

    private static void AppendFileHash(IncrementalHash hash, string path)
    {
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var digest = SHA256.HashData(file);
        hash.AppendData(digest);
    }

    private static bool TryGetCachedAgent(
        string backend,
        string fingerprint,
        out string agentPath,
        out string protocolPath)
    {
        var cacheDirectory = GetCacheDirectory(backend, fingerprint);
        var projectFolder = GetProjectFolder(backend);
        agentPath = Path.Combine(cacheDirectory, projectFolder + ".dll");
        protocolPath = Path.Combine(cacheDirectory, "U3DViewer.Protocol.dll");

        return File.Exists(agentPath) && new FileInfo(agentPath).Length > 0 &&
               File.Exists(protocolPath) && new FileInfo(protocolPath).Length > 0;
    }

    private static string GetCacheDirectory(string backend, string fingerprint) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "U3DViewer",
            "AgentCache",
            backend,
            fingerprint);

    private static string GetSourceRoot() =>
        Path.Combine(AppContext.BaseDirectory, "agent-builder");

    private static string GetProjectFolder(string backend) =>
        backend == "Mono" ? "U3DViewer.Agent.Mono" : "U3DViewer.Agent.IL2CPP";

    private static string ShortFingerprint(string fingerprint) =>
        fingerprint.Length <= 12 ? fingerprint : fingerprint[..12];

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
