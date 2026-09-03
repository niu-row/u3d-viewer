using BepInEx.Logging;
using U3DViewer.Protocol;
using UnityEngine;

namespace U3DViewer.Agent.IL2CPP;

public sealed class RuntimeBehaviour : MonoBehaviour
{
    private const float SnapshotRestartDelay = 1.0f;
    private const int HierarchyNodesPerFrame = 64;
    private const double HierarchyScanBudgetMilliseconds = 0.75;

    private static PipeServer? _pipeServer;
    private static ManualLogSource? _log;
    private static SceneCameraController? _sceneCamera;
    private static SceneScanner.SceneScanSession? _sceneScan;
    private static Task<string>? _snapshotSerialization;
    private static readonly HashSet<int> ExpandedInstanceIds = new();
    private static float _nextSnapshotAt;
    private static long _sequence;
    private static int _selectedInstanceId;
    private static bool _originalRunInBackground;
    private static bool _runInBackgroundCaptured;

    public RuntimeBehaviour(IntPtr pointer) : base(pointer)
    {
    }

    internal static void Initialize(PipeServer pipeServer, ManualLogSource log)
    {
        _originalRunInBackground = Application.runInBackground;
        _runInBackgroundCaptured = true;
        Application.runInBackground = true;

        _pipeServer = pipeServer;
        _log = log;
        _sceneCamera = new SceneCameraController();
        _sceneScan = null;
        _snapshotSerialization = null;
        ExpandedInstanceIds.Clear();
        _nextSnapshotAt = 0f;
        _sequence = 0;
        _selectedInstanceId = 0;
        _log.LogInfo("Background execution forced on for U3DViewer mode.");
    }

    internal static void Shutdown()
    {
        RestoreBackgroundExecution();
        ResetSnapshotState();
        _pipeServer = null;
        _log = null;
    }

    public void Update()
    {
        var pipeServer = _pipeServer;
        if (pipeServer is null || !pipeServer.IsViewerConnected)
        {
            ResetSnapshotState();
            ExpandedInstanceIds.Clear();
            _nextSnapshotAt = 0f;
            return;
        }

        if (!Application.runInBackground)
        {
            Application.runInBackground = true;
        }

        while (pipeServer.TryDequeueCommand(out var command))
        {
            try
            {
                switch (command.Kind)
                {
                    case ViewerCommandKind.SelectObject:
                        _selectedInstanceId = command.InstanceId;
                        RestartHierarchyScan();
                        continue;
                    case ViewerCommandKind.HierarchyExpanded:
                        if (command.Flag)
                        {
                            ExpandedInstanceIds.Add(command.InstanceId);
                        }
                        else
                        {
                            ExpandedInstanceIds.Remove(command.InstanceId);
                        }
                        RestartHierarchyScan();
                        continue;
                    default:
                        _sceneCamera?.Apply(command);
                        break;
                }
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"Failed to apply IL2CPP viewer command {command.Kind}: {ex.Message}");
            }
        }

        _sceneCamera?.TickRender();
        PublishCompletedSerialization(pipeServer);

        if (_snapshotSerialization is not null)
        {
            return;
        }

        var now = Time.unscaledTime;
        if (_sceneScan is null)
        {
            if (now < _nextSnapshotAt)
            {
                return;
            }

            try
            {
                _sceneScan = SceneScanner.Begin(
                    ++_sequence,
                    _selectedInstanceId,
                    new HashSet<int>(ExpandedInstanceIds));
            }
            catch (Exception ex)
            {
                _nextSnapshotAt = now + SnapshotRestartDelay;
                _log?.LogError($"Failed to start IL2CPP scene scan: {ex}");
                return;
            }
        }

        try
        {
            _sceneScan.ProcessSlice(HierarchyNodesPerFrame, HierarchyScanBudgetMilliseconds);
            if (!_sceneScan.IsComplete)
            {
                return;
            }

            var snapshot = _sceneScan.Snapshot;
            snapshot.RenderTarget = _sceneCamera?.GetRenderTargetInfo();
            _sceneScan = null;
            _nextSnapshotAt = Time.unscaledTime + SnapshotRestartDelay;

            _snapshotSerialization = Task.Run(() => JsonSnapshotWriter.Write(snapshot));
        }
        catch (Exception ex)
        {
            _sceneScan = null;
            _nextSnapshotAt = Time.unscaledTime + SnapshotRestartDelay;
            _log?.LogError($"Failed to advance IL2CPP scene scan: {ex}");
        }
    }

    public void OnDestroy()
    {
        RestoreBackgroundExecution();
        ResetSnapshotState();
        ExpandedInstanceIds.Clear();
        _sceneCamera?.Dispose();
        _sceneCamera = null;
    }

    private static void PublishCompletedSerialization(PipeServer pipeServer)
    {
        var task = _snapshotSerialization;
        if (task is null || !task.IsCompleted)
        {
            return;
        }

        _snapshotSerialization = null;
        if (task.Status == TaskStatus.RanToCompletion)
        {
            pipeServer.Publish(task.Result);
            return;
        }

        if (task.IsFaulted)
        {
            _log?.LogError($"Failed to serialize IL2CPP scene snapshot: {task.Exception?.GetBaseException().Message}");
        }
    }

    private static void RestartHierarchyScan()
    {
        _sceneScan = null;
        _snapshotSerialization = null;
        _nextSnapshotAt = 0f;
    }

    private static void ResetSnapshotState()
    {
        _sceneScan = null;
        _snapshotSerialization = null;
    }

    private static void RestoreBackgroundExecution()
    {
        if (!_runInBackgroundCaptured)
        {
            return;
        }

        Application.runInBackground = _originalRunInBackground;
        _runInBackgroundCaptured = false;
    }
}
