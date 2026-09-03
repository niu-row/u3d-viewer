using System.Diagnostics;
using System.Text;
using BepInEx.Logging;
using U3DViewer.Protocol;
using UnityEngine;

namespace U3DViewer.Agent.IL2CPP;

public sealed class RuntimeBehaviour : MonoBehaviour
{
    private const float SnapshotRestartDelay = 1.0f;
    private const int HierarchyNodesPerFrame = 64;
    private const double HierarchyScanBudgetMilliseconds = 0.75;
    private const int InteractiveHierarchyNodesPerFrame = 256;
    private const double InteractiveHierarchyScanBudgetMilliseconds = 2.0;

    private static PipeServer? _pipeServer;
    private static ManualLogSource? _log;
    private static SceneCameraController? _sceneCamera;
    private static SceneCullingController? _sceneCulling;
    private static SceneScanner.SceneScanSession? _sceneScan;
    private static Task<SerializedSnapshot>? _snapshotSerialization;
    private static readonly HashSet<int> ExpandedInstanceIds = new();
    private static float _nextSnapshotAt;
    private static long _sequence;
    private static int _selectedInstanceId;
    private static bool _interactiveHierarchyRefresh;
    private static int _currentScanNodes;
    private static double _currentScanMs;
    private static double _lastScanMs;
    private static double _averageScanMs;
    private static double _maxScanMs;
    private static long _scanSamples;
    private static int _lastScanNodes;
    private static double _lastSerializeMs;
    private static int _lastSnapshotBytes;
    private static float _gameFpsWindowStart;
    private static int _gameFpsWindowFrames;
    private static double _gameFps;
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
        _sceneCulling = new SceneCullingController();
        _sceneScan = null;
        _snapshotSerialization = null;
        ExpandedInstanceIds.Clear();
        _nextSnapshotAt = 0f;
        _sequence = 0;
        _selectedInstanceId = 0;
        _interactiveHierarchyRefresh = false;
        ResetPerformanceMetrics();
        _log.LogInfo("Background execution forced on for U3DViewer mode.");
    }

    internal static void Shutdown()
    {
        RestoreBackgroundExecution();
        ResetSnapshotState();
        _sceneCulling = null;
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
            _interactiveHierarchyRefresh = false;
            return;
        }

        if (!Application.runInBackground)
        {
            Application.runInBackground = true;
        }

        UpdateGameFps();
        var flushSceneTransport = false;

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
                            ExpandedInstanceIds.Add(command.InstanceId);
                        }
                        else
                        {
                            ExpandedInstanceIds.Remove(command.InstanceId);
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

                        if (command.Kind == ViewerCommandKind.CameraReset ||
                            command.Kind == ViewerCommandKind.CameraRecover ||
                            command.Kind == ViewerCommandKind.CameraStreamSettings ||
                            (command.Kind == ViewerCommandKind.CameraVisibility && command.Flag))
                        {
                            flushSceneTransport = true;
                        }

                        if (command.Kind == ViewerCommandKind.CameraStreamSettings ||
                            command.Kind == ViewerCommandKind.CameraRecover)
                        {
                            _interactiveHierarchyRefresh = true;
                            RestartHierarchyScan();
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"Failed to apply IL2CPP viewer command {command.Kind}: {ex.Message}");
            }
        }

        _sceneCamera?.TickRender();
        if (flushSceneTransport)
        {
            try
            {
                GL.Flush();
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"Failed to flush IL2CPP Scene transport bootstrap commands: {ex.Message}");
            }
        }

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
                    new HashSet<int>(ExpandedInstanceIds));
            }
            catch (Exception ex)
            {
                _nextSnapshotAt = now + SnapshotRestartDelay;
                _interactiveHierarchyRefresh = false;
                _log?.LogError($"Failed to start IL2CPP scene scan: {ex}");
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
            _log?.LogError($"Failed to advance IL2CPP scene scan: {ex}");
        }
    }

    public void OnDestroy()
    {
        RestoreBackgroundExecution();
        ResetSnapshotState();
        ExpandedInstanceIds.Clear();
        _interactiveHierarchyRefresh = false;
        _sceneCulling = null;
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
            var result = task.Result;
            _lastSerializeMs = result.SerializeMs;
            _lastSnapshotBytes = result.Bytes;
            pipeServer.Publish(result.Json);
            return;
        }

        if (task.IsFaulted)
        {
            _log?.LogError($"Failed to serialize IL2CPP scene snapshot: {task.Exception?.GetBaseException().Message}");
        }
    }

    private static SerializedSnapshot SerializeSnapshot(SceneSnapshot snapshot)
    {
        var started = Stopwatch.GetTimestamp();
        var json = JsonSnapshotWriter.Write(snapshot);
        var serializeMs = ElapsedMilliseconds(started);
        return new SerializedSnapshot(json, Encoding.UTF8.GetByteCount(json), serializeMs);
    }

    private static PerformanceInfo BuildPerformanceInfo() => new()
    {
        GameFps = _gameFps,
        HierarchyNodes = _lastScanNodes,
        HierarchyScanMs = _lastScanMs,
        HierarchyScanAverageMs = _averageScanMs,
        HierarchyScanMaxMs = _maxScanMs,
        SnapshotSerializeMs = _lastSerializeMs,
        SnapshotBytes = _lastSnapshotBytes
    };

    private static void UpdateGameFps()
    {
        var now = Time.unscaledTime;
        if (_gameFpsWindowStart <= 0f)
        {
            _gameFpsWindowStart = now;
            _gameFpsWindowFrames = 0;
        }

        _gameFpsWindowFrames++;
        var elapsed = now - _gameFpsWindowStart;
        if (elapsed < 0.5f)
        {
            return;
        }

        _gameFps = elapsed > 0f ? _gameFpsWindowFrames / elapsed : 0d;
        _gameFpsWindowStart = now;
        _gameFpsWindowFrames = 0;
    }

    private static void RecordHierarchyScan(int nodes, double milliseconds)
    {
        _lastScanNodes = nodes;
        _lastScanMs = milliseconds;
        _scanSamples++;
        _averageScanMs += (milliseconds - _averageScanMs) / _scanSamples;
        _maxScanMs = Math.Max(_maxScanMs, milliseconds);
    }

    private static double ElapsedMilliseconds(long started) =>
        (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;

    private static void RestartHierarchyScan()
    {
        _sceneScan = null;
        _snapshotSerialization = null;
        _currentScanNodes = 0;
        _currentScanMs = 0;
        _nextSnapshotAt = 0f;
    }

    private static void ResetSnapshotState()
    {
        _sceneScan = null;
        _snapshotSerialization = null;
        _currentScanNodes = 0;
        _currentScanMs = 0;
    }

    private static void ResetPerformanceMetrics()
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
        _gameFpsWindowStart = 0f;
        _gameFpsWindowFrames = 0;
        _gameFps = 0d;
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
