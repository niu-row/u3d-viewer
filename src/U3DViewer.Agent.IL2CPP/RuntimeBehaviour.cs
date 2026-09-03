using BepInEx.Logging;
using UnityEngine;

namespace U3DViewer.Agent.IL2CPP;

public sealed class RuntimeBehaviour : MonoBehaviour
{
    private static PipeServer? _pipeServer;
    private static ManualLogSource? _log;
    private static SceneCameraController? _sceneCamera;
    private static float _nextSnapshotAt;
    private static long _sequence;
    private static int _selectedInstanceId;

    public RuntimeBehaviour(IntPtr pointer) : base(pointer)
    {
    }

    internal static void Initialize(PipeServer pipeServer, ManualLogSource log)
    {
        _pipeServer = pipeServer;
        _log = log;
        _sceneCamera = new SceneCameraController();
        _nextSnapshotAt = 0f;
        _sequence = 0;
        _selectedInstanceId = 0;
    }

    internal static void Shutdown()
    {
        _pipeServer = null;
        _log = null;
    }

    public void Update()
    {
        var pipeServer = _pipeServer;
        if (pipeServer is null)
        {
            return;
        }

        while (pipeServer.TryDequeueCommand(out var command))
        {
            try
            {
                if (command.Kind == U3DViewer.Protocol.ViewerCommandKind.SelectObject)
                {
                    _selectedInstanceId = command.InstanceId;
                    _nextSnapshotAt = 0f;
                    continue;
                }

                _sceneCamera?.Apply(command);
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"Failed to apply IL2CPP viewer command {command.Kind}: {ex.Message}");
            }
        }

        _sceneCamera?.TickRender();

        if (Time.unscaledTime < _nextSnapshotAt)
        {
            return;
        }

        _nextSnapshotAt = Time.unscaledTime + 1.0f;

        try
        {
            var snapshot = SceneScanner.Capture(++_sequence, _selectedInstanceId);
            snapshot.RenderTarget = _sceneCamera?.GetRenderTargetInfo();
            pipeServer.Publish(JsonSnapshotWriter.Write(snapshot));
        }
        catch (Exception ex)
        {
            _log?.LogError($"Failed to capture IL2CPP scene snapshot: {ex}");
        }
    }

    public void OnDestroy()
    {
        _sceneCamera?.Dispose();
        _sceneCamera = null;
    }
}
