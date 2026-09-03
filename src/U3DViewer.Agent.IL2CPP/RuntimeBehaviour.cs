using BepInEx.Logging;
using UnityEngine;

namespace U3DViewer.Agent.IL2CPP;

public sealed class RuntimeBehaviour : MonoBehaviour
{
    private static PipeServer? _pipeServer;
    private static ManualLogSource? _log;
    private static float _nextSnapshotAt;
    private static long _sequence;

    public RuntimeBehaviour(IntPtr pointer) : base(pointer)
    {
    }

    internal static void Initialize(PipeServer pipeServer, ManualLogSource log)
    {
        _pipeServer = pipeServer;
        _log = log;
        _nextSnapshotAt = 0f;
        _sequence = 0;
    }

    internal static void Shutdown()
    {
        _pipeServer = null;
        _log = null;
    }

    public void Update()
    {
        var pipeServer = _pipeServer;
        if (pipeServer is null || Time.unscaledTime < _nextSnapshotAt)
        {
            return;
        }

        _nextSnapshotAt = Time.unscaledTime + 1.0f;

        try
        {
            var snapshot = SceneScanner.Capture(++_sequence);
            pipeServer.Publish(JsonSnapshotWriter.Write(snapshot));
        }
        catch (Exception ex)
        {
            _log?.LogError($"Failed to capture IL2CPP scene snapshot: {ex}");
        }
    }
}
