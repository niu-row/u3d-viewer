using System.Diagnostics;
using BepInEx;
using BepInEx.Unity.IL2CPP;

namespace U3DViewer.Agent.IL2CPP;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "dev.u3dviewer.agent.il2cpp";
    public const string PluginName = "U3D Viewer Agent (IL2CPP)";
    public const string PluginVersion = "0.1.0";

    private PipeServer? _pipeServer;

    public override void Load()
    {
        var pipeName = $"u3d-viewer-{Process.GetCurrentProcess().Id}";
        _pipeServer = new PipeServer(pipeName, Log);
        _pipeServer.Start();

        RuntimeBehaviour.Initialize(_pipeServer, Log);
        AddComponent<RuntimeBehaviour>();

        Log.LogInfo($"U3D Viewer IL2CPP agent loaded. Pipe: {pipeName}");
    }

    public override bool Unload()
    {
        RuntimeBehaviour.Shutdown();
        _pipeServer?.Dispose();
        _pipeServer = null;
        return true;
    }
}
