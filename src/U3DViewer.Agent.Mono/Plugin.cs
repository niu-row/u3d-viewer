using System.Diagnostics;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.Mono;
using UnityEngine;

namespace U3DViewer.Agent.Mono;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "dev.u3dviewer.agent.mono";
    public const string PluginName = "U3D Viewer Agent (Mono)";
    public const string PluginVersion = "0.1.0";

    private const float SnapshotRestartDelay = 1.0f;
    private const int HierarchyNodesPerFrame = 64;
    private const double HierarchyScanBudgetMilliseconds = 0.75;

    private PipeServer? _pipeServer;
    private SceneCameraController? _sceneCamera;
    private SceneScanner.SceneScanSession? _sceneScan;
    private Task<string>? _snapshotSerialization;
    private float _nextSnapshotAt;
    private long _sequence;
    private int _selectedInstanceId;
    private bool _originalRunInBackground;
    private bool _runInBackgroundCaptured;

    private ManualLogSource LogSource => Logger;

    private void Awake()
    {
        _originalRunInBackground = Application.runInBackground;
        _runInBackgroundCaptured = true;
        Application.runInBackground = true;

        var pipeName = $"u3d-viewer-{Process.GetCurrentProcess().Id}";
        _sceneCamera = new SceneCameraController();
        _pipeServer = new PipeServer(pipeName, LogSource);
        _pipeServer.Start();
        _sceneScan = null;
        _snapshotSerialization = null;
        _selectedInstanceId = 0;
        _nextSnapshotAt = 0f;
        LogSource.LogInfo($"U3D Viewer Mono agent loaded. Pipe: {pipeName}. Background execution forced on for Viewer mode.");
    }

    private void Update()
    {
        var pipeServer = _pipeServer;
        if (pipeServer is null || !pipeServer.IsViewerConnected)
        {
            _sceneScan = null;
            _snapshotSerialization = null;
            _nextSnapshotAt = 0f;
            return;
        }

        // Some games change this setting after startup. Viewer mode requires the Unity
        // player loop to keep running while its window is not focused.
        if (!Application.runInBackground)
        {
            Application.runInBackground = true;
        }

        while (pipeServer.TryDequeueCommand(out var command))
        {
            try
            {
                if (command.Kind == U3DViewer.Protocol.ViewerCommandKind.SelectObject)
                {
                    _selectedInstanceId = command.InstanceId;
                    _sceneScan = null;
                    _snapshotSerialization = null;
                    _nextSnapshotAt = 0f;
                    continue;
                }

                _sceneCamera?.Apply(command);
            }
            catch (Exception ex)
            {
                LogSource.LogWarning($"Failed to apply viewer command {command.Kind}: {ex.Message}");
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
                _sceneScan = SceneScanner.Begin(++_sequence, _selectedInstanceId);
            }
            catch (Exception ex)
            {
                _nextSnapshotAt = now + SnapshotRestartDelay;
                LogSource.LogError($"Failed to start scene scan: {ex}");
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

            // Snapshot data contains no live Unity objects after the incremental scan completes,
            // so JSON construction can safely run away from the Unity main thread.
            _snapshotSerialization = Task.Run(() => JsonSnapshotWriter.Write(snapshot));
        }
        catch (Exception ex)
        {
            _sceneScan = null;
            _nextSnapshotAt = Time.unscaledTime + SnapshotRestartDelay;
            LogSource.LogError($"Failed to advance scene scan: {ex}");
        }
    }

    private void PublishCompletedSerialization(PipeServer pipeServer)
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
            LogSource.LogError($"Failed to serialize scene snapshot: {task.Exception?.GetBaseException().Message}");
        }
    }

    private void OnDestroy()
    {
        if (_runInBackgroundCaptured)
        {
            Application.runInBackground = _originalRunInBackground;
            _runInBackgroundCaptured = false;
        }

        _sceneScan = null;
        _snapshotSerialization = null;
        _sceneCamera?.Dispose();
        _sceneCamera = null;
        _pipeServer?.Dispose();
        _pipeServer = null;
    }
}
