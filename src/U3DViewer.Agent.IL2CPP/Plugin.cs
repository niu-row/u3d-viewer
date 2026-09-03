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
        _pipeServer = new PipeServer("u3d-viewer", Log);
        _pipeServer.Start();

        RuntimeBehaviour.Initialize(_pipeServer, Log);
        AddComponent<RuntimeBehaviour>();

        Log.LogInfo("U3D Viewer IL2CPP agent loaded.");
    }

    public override bool Unload()
    {
        RuntimeBehaviour.Shutdown();
        _pipeServer?.Dispose();
        _pipeServer = null;
        return true;
    }
}
