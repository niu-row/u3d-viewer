using System.Diagnostics;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.Mono;
using U3DViewer.Protocol;
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
    private const int InteractiveHierarchyNodesPerFrame = 256;
    private const double InteractiveHierarchyScanBudgetMilliseconds = 2.0;

    private readonly HashSet<int> _expandedInstanceIds = new();
    private PipeServer? _pipeServer;
    private SceneCameraController? _sceneCamera;
    private SceneCullingController? _sceneCulling;
    private SceneScanner.SceneScanSession? _sceneScan;
    private Task<SerializedSnapshot>? _snapshotSerialization;
    private float _nextSnapshotAt;
    private long _sequence;
    private int _selectedInstanceId;
    private bool _interactiveHierarchyRefresh;
    private int _currentScanNodes;
    private double _currentScanMs;
    private double _lastScanMs;
    private double _averageScanMs;
    private double _maxScanMs;
    private long _scanSamples;
    private int _lastScanNodes;
    private double _lastSerializeMs;
    private int _lastSnapshotBytes;
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
        _sceneCulling = new SceneCullingController();
        _pipeServer = new PipeServer(pipeName, LogSource);
        _pipeServer.Start();
        _sceneScan = null;
        _snapshotSerialization = null;
        _expandedInstanceIds.Clear();
        _selectedInstanceId = 0;
        _nextSnapshotAt = 0f;
        _interactiveHierarchyRefresh = false;
        ResetPerformanceMetrics();
        LogSource.LogInfo($"U3D Viewer Mono agent loaded. Pipe: {pipeName}. Background execution forced on for Viewer mode.");
    }

    private void Update()
    {
        var pipeServer = _pipeServer;
        if (pipeServer is null || !pipeServer.IsViewerConnected)
        {
            ResetSnapshotState();
            _expandedInstanceIds.Clear();
            _nextSnapshotAt = 0f;
            _interactiveHierarchyRefresh = false;
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
                        _interactiveHierarchyRefresh = true;
                        RestartHierarchyScan();
                        continue;
                    case ViewerCommandKind.HierarchyExpanded:
                        if (command.Flag)
                        {
                            _expandedInstanceIds.Add(command.InstanceId);
                        }
                        else
                        {
                            _expandedInstanceIds.Remove(command.InstanceId);
                        }
                        _interactiveHierarchyRefresh = true;
                        RestartHierarchyScan();
                        continue;
                    default:
                        _sceneCamera?.Apply(command);
                        if (command.Kind == ViewerCommandKind.CameraCullingMask)
                        {
                            _sceneCulling?.Apply(command);
                        }
                        else if (command.Kind == ViewerCommandKind.CameraReset)
                        {
                            _sceneCulling?.Reapply();
                        }
                        break;
                }
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
                _currentScanNodes = 0;
                _currentScanMs = 0;
                _sceneScan = SceneScanner.Begin(
                    ++_sequence,
                    _selectedInstanceId,
                    new HashSet<int>(_expandedInstanceIds));
            }
            catch (Exception ex)
            {
                _nextSnapshotAt = now + SnapshotRestartDelay;
                _interactiveHierarchyRefresh = false;
                LogSource.LogError($"Failed to start scene scan: {ex}");
                return;
            }
        }

        try
        {
            var maxNodes = _interactiveHierarchyRefresh
                ? InteractiveHierarchyNodesPerFrame
                : HierarchyNodesPerFrame;
            var budgetMilliseconds = _interactiveHierarchyRefresh
                ? InteractiveHierarchyScanBudgetMilliseconds
                : HierarchyScanBudgetMilliseconds;

            var scanStarted = Stopwatch.GetTimestamp();
            var processed = _sceneScan.ProcessSlice(maxNodes, budgetMilliseconds);
            _currentScanNodes += processed;
            _currentScanMs += ElapsedMilliseconds(scanStarted);

            if (!_sceneScan.IsComplete)
            {
                return;
            }

            RecordHierarchyScan(_currentScanNodes, _currentScanMs);

            var snapshot = _sceneScan.Snapshot;
            var renderTarget = _sceneCamera?.GetRenderTargetInfo();
            _sceneCulling?.Populate(renderTarget);
            snapshot.RenderTarget = renderTarget;
            snapshot.Performance = BuildPerformanceInfo();
            _sceneCamera?.PopulatePerformance(snapshot.Performance);
            _sceneScan = null;
            _nextSnapshotAt = Time.unscaledTime + SnapshotRestartDelay;
            _interactiveHierarchyRefresh = false;
            _currentScanNodes = 0;
            _currentScanMs = 0;

            _snapshotSerialization = Task.Run(() => SerializeSnapshot(snapshot));
        }
        catch (Exception ex)
        {
            _sceneScan = null;
            _nextSnapshotAt = Time.unscaledTime + SnapshotRestartDelay;
            _interactiveHierarchyRefresh = false;
            _currentScanNodes = 0;
            _currentScanMs = 0;
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
            var result = task.Result;
            _lastSerializeMs = result.SerializeMs;
            _lastSnapshotBytes = result.Bytes;
            pipeServer.Publish(result.Json);
            return;
        }

        if (task.IsFaulted)
        {
            LogSource.LogError($"Failed to serialize scene snapshot: {task.Exception?.GetBaseException().Message}");
        }
    }

    private static SerializedSnapshot SerializeSnapshot(SceneSnapshot snapshot)
    {
        var started = Stopwatch.GetTimestamp();
        var json = JsonSnapshotWriter.Write(snapshot);
        var serializeMs = ElapsedMilliseconds(started);
        return new SerializedSnapshot(json, Encoding.UTF8.GetByteCount(json), serializeMs);
    }

    private PerformanceInfo BuildPerformanceInfo() => new()
    {
        HierarchyNodes = _lastScanNodes,
        HierarchyScanMs = _lastScanMs,
        HierarchyScanAverageMs = _averageScanMs,
        HierarchyScanMaxMs = _maxScanMs,
        SnapshotSerializeMs = _lastSerializeMs,
        SnapshotBytes = _lastSnapshotBytes
    };

    private void RecordHierarchyScan(int nodes, double milliseconds)
    {
        _lastScanNodes = nodes;
        _lastScanMs = milliseconds;
        _scanSamples++;
        _averageScanMs += (milliseconds - _averageScanMs) / _scanSamples;
        _maxScanMs = Math.Max(_maxScanMs, milliseconds);
    }

    private static double ElapsedMilliseconds(long started) =>
        (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;

    private void RestartHierarchyScan()
    {
        _sceneScan = null;
        _snapshotSerialization = null;
        _currentScanNodes = 0;
        _currentScanMs = 0;
        _nextSnapshotAt = 0f;
    }

    private void ResetSnapshotState()
    {
        _sceneScan = null;
        _snapshotSerialization = null;
        _currentScanNodes = 0;
        _currentScanMs = 0;
    }

    private void ResetPerformanceMetrics()
    {
        _currentScanNodes = 0;
        _currentScanMs = 0;
        _lastScanMs = 0;
        _averageScanMs = 0;
        _maxScanMs = 0;
        _scanSamples = 0;
        _lastScanNodes = 0;
        _lastSerializeMs = 0;
        _lastSnapshotBytes = 0;
    }

    private void OnDestroy()
    {
        if (_runInBackgroundCaptured)
        {
            Application.runInBackground = _originalRunInBackground;
            _runInBackgroundCaptured = false;
        }

        ResetSnapshotState();
        _expandedInstanceIds.Clear();
        _interactiveHierarchyRefresh = false;
        _sceneCulling = null;
        _sceneCamera?.Dispose();
        _sceneCamera = null;
        _pipeServer?.Dispose();
        _pipeServer = null;
    }

    private sealed class SerializedSnapshot
    {
        public SerializedSnapshot(string json, int bytes, double serializeMs)
        {
            Json = json;
            Bytes = bytes;
            SerializeMs = serializeMs;
        }

        public string Json { get; }
        public int Bytes { get; }
        public double SerializeMs { get; }
    }
}
