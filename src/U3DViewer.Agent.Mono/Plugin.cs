using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.Mono;

namespace U3DViewer.Agent.Mono;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "dev.u3dviewer.agent.mono";
    public const string PluginName = "U3D Viewer Agent (Mono)";
    public const string PluginVersion = "0.1.0";

    private PipeServer? _pipeServer;
    private float _nextSnapshotAt;
    private long _sequence;

    private ManualLogSource LogSource => Logger;

    private void Awake()
    {
        _pipeServer = new PipeServer("u3d-viewer", LogSource);
        _pipeServer.Start();
        LogSource.LogInfo("U3D Viewer Mono agent loaded.");
    }

    private void Update()
    {
        if (_pipeServer is null || UnityEngine.Time.unscaledTime < _nextSnapshotAt)
        {
            return;
        }

        _nextSnapshotAt = UnityEngine.Time.unscaledTime + 1.0f;

        try
        {
            var snapshot = SceneScanner.Capture(++_sequence);
            _pipeServer.Publish(JsonSnapshotWriter.Write(snapshot));
        }
        catch (Exception ex)
        {
            LogSource.LogError($"Failed to capture scene snapshot: {ex}");
        }
    }

    private void OnDestroy()
    {
        _pipeServer?.Dispose();
        _pipeServer = null;
    }
}
