using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
#if !LEGACY_MONO
using System.Threading.Tasks;
#endif
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

    private readonly HashSet<int> _expandedInstanceIds = new HashSet<int>();
    private PipeServer? _pipeServer;
    private SceneCameraController? _sceneCamera;
    private SceneCullingController? _sceneCulling;
    private SceneScanner.SceneScanSession? _sceneScan;
#if !LEGACY_MONO
    private Task<SerializedSnapshot>? _snapshotSerialization;
#endif
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
    private float _gameFpsWindowStart;
    private int _gameFpsWindowFrames;
    private double _gameFps;
    private bool _originalRunInBackground;
    private bool _runInBackgroundCaptured;
    private bool _viewerSessionActive;

    private ManualLogSource LogSource => Logger;

    private void Awake()
    {
        // Startup must remain passive. In particular, do not touch Application state,
        // layer metadata, cameras, RenderTextures, or the native bridge before a real
        // Viewer connection exists. Older Unity/Mono players can be sensitive to Unity
        // API calls made this early in startup.
        var pipeName = $"u3d-viewer-{Process.GetCurrentProcess().Id}";
        _sceneCamera = null;
        _sceneCulling = null;
        _pipeServer = new PipeServer(pipeName, LogSource);
        _pipeServer.Start();
        _sceneScan = null;
#if !LEGACY_MONO
        _snapshotSerialization = null;
#endif
        _expandedInstanceIds.Clear();
        _selectedInstanceId = 0;
        _nextSnapshotAt = 0f;
        _interactiveHierarchyRefresh = false;
        _runInBackgroundCaptured = false;
        _viewerSessionActive = false;
        ResetPerformanceMetrics();
#if LEGACY_MONO
        LogSource.LogInfo($"U3D Viewer Mono agent loaded in legacy CLR 2.0/.NET 3.5 mode. Pipe: {pipeName}. Passive until Viewer connects.");
#else
        LogSource.LogInfo($"U3D Viewer Mono agent loaded. Pipe: {pipeName}. Passive until Viewer connects.");
#endif
    }

    private void Update()
    {
        var pipeServer = _pipeServer;
        if (pipeServer is null || !pipeServer.IsViewerConnected)
        {
            if (_viewerSessionActive)
            {
                EndViewerSession();
            }
            else
            {
                ResetDisconnectedState();
            }
            return;
        }

        if (!_viewerSessionActive)
        {
            BeginViewerSession();
        }

        if (!Application.runInBackground)
        {
            Application.runInBackground = true;
        }

        UpdateGameFps();
#if !LEGACY_MONO
        var flushSceneTransport = false;
#endif

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

#if !LEGACY_MONO
                        if (command.Kind == ViewerCommandKind.CameraReset ||
                            command.Kind == ViewerCommandKind.CameraRecover ||
                            command.Kind == ViewerCommandKind.CameraStreamSettings ||
                            (command.Kind == ViewerCommandKind.CameraVisibility && command.Flag))
                        {
                            flushSceneTransport = true;
                        }
#endif

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
                LogSource.LogWarning($"Failed to apply viewer command {command.Kind}: {ex.Message}");
            }
        }

        _sceneCamera?.TickRender();
#if !LEGACY_MONO
        if (flushSceneTransport)
        {
            try
            {
                GL.Flush();
            }
            catch (Exception ex)
            {
                LogSource.LogWarning($"Failed to flush Scene transport bootstrap commands: {ex.Message}");
            }
        }
#endif

#if !LEGACY_MONO
        PublishCompletedSerialization(pipeServer);

        if (_snapshotSerialization is not null)
        {
            return;
        }
#endif

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

#if LEGACY_MONO
            var serialized = SerializeSnapshot(snapshot);
            _lastSerializeMs = serialized.SerializeMs;
            _lastSnapshotBytes = serialized.Bytes;
            pipeServer.Publish(serialized.Json);
#else
            _snapshotSerialization = Task.Run(() => SerializeSnapshot(snapshot));
#endif
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

    private void BeginViewerSession()
    {
        if (_viewerSessionActive)
        {
            return;
        }

        _originalRunInBackground = Application.runInBackground;
        _runInBackgroundCaptured = true;
        Application.runInBackground = true;

        _sceneCamera = new SceneCameraController();
        _sceneCulling = new SceneCullingController();
        _viewerSessionActive = true;
        _nextSnapshotAt = 0f;
        ResetPerformanceMetrics();
        LogSource.LogInfo("Viewer session activated.");
    }

    private void EndViewerSession()
    {
        if (!_viewerSessionActive)
        {
            ResetDisconnectedState();
            return;
        }

        ResetDisconnectedState();
        _sceneCulling = null;
        _sceneCamera?.Dispose();
        _sceneCamera = null;

        if (_runInBackgroundCaptured)
        {
            Application.runInBackground = _originalRunInBackground;
            _runInBackgroundCaptured = false;
        }

        _viewerSessionActive = false;
        LogSource.LogInfo("Viewer session deactivated; game state restored.");
    }

    private void ResetDisconnectedState()
    {
        ResetSnapshotState();
        _expandedInstanceIds.Clear();
        _selectedInstanceId = 0;
        _nextSnapshotAt = 0f;
        _interactiveHierarchyRefresh = false;
    }

#if !LEGACY_MONO
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
#endif

    private static SerializedSnapshot SerializeSnapshot(SceneSnapshot snapshot)
    {
        var started = Stopwatch.GetTimestamp();
        var json = JsonSnapshotWriter.Write(snapshot);
        var serializeMs = ElapsedMilliseconds(started);
        return new SerializedSnapshot(json, Encoding.UTF8.GetByteCount(json), serializeMs);
    }

    private PerformanceInfo BuildPerformanceInfo() => new PerformanceInfo
    {
        GameFps = _gameFps,
        HierarchyNodes = _lastScanNodes,
        HierarchyScanMs = _lastScanMs,
        HierarchyScanAverageMs = _averageScanMs,
        HierarchyScanMaxMs = _maxScanMs,
        SnapshotSerializeMs = _lastSerializeMs,
        SnapshotBytes = _lastSnapshotBytes
    };

    private void UpdateGameFps()
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
#if !LEGACY_MONO
        _snapshotSerialization = null;
#endif
        _currentScanNodes = 0;
        _currentScanMs = 0;
        _nextSnapshotAt = 0f;
    }

    private void ResetSnapshotState()
    {
        _sceneScan = null;
#if !LEGACY_MONO
        _snapshotSerialization = null;
#endif
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
        _gameFpsWindowStart = 0f;
        _gameFpsWindowFrames = 0;
        _gameFps = 0d;
    }

    private void OnDestroy()
    {
        EndViewerSession();
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

        public string Json { get; private set; }
        public int Bytes { get; private set; }
        public double SerializeMs { get; private set; }
    }
}
