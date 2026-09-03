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

    private PipeServer? _pipeServer;
    private SceneCameraController? _sceneCamera;
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
        _selectedInstanceId = 0;
        LogSource.LogInfo($"U3D Viewer Mono agent loaded. Pipe: {pipeName}. Background execution forced on for Viewer mode.");
    }

    private void Update()
    {
        var pipeServer = _pipeServer;
        if (pipeServer is null || !pipeServer.IsViewerConnected)
        {
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
            LogSource.LogError($"Failed to capture scene snapshot: {ex}");
        }
    }

    private void OnDestroy()
    {
        if (_runInBackgroundCaptured)
        {
            Application.runInBackground = _originalRunInBackground;
            _runInBackgroundCaptured = false;
        }

        _sceneCamera?.Dispose();
        _sceneCamera = null;
        _pipeServer?.Dispose();
        _pipeServer = null;
    }
}
