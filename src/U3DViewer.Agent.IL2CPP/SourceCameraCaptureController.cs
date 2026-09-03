using System.Collections.Generic;
using System.Diagnostics;
using U3DViewer.Protocol;
using UnityEngine;
using UnityEngine.Rendering;

namespace U3DViewer.Agent.IL2CPP;

internal sealed class SourceCameraCaptureController : IDisposable
{
    private const int DefaultRenderWidth = 1280;
    private const int DefaultRenderHeight = 720;
    private const float DefaultIdleFps = 15f;
    private const float DefaultInteractiveFps = 30f;
    private const float MinStreamFps = 1f;
    private const float MaxStreamFps = 120f;
    private const int MinRenderDimension = 64;
    private const int MaxRenderDimension = 4096;
    private const string ViewerCameraObjectName = "__U3DViewerCamera";

    private readonly System.Action<ScriptableRenderContext, Camera> _managedEndCameraRenderingHandler;
    private readonly Il2CppSystem.Action<ScriptableRenderContext, Camera> _endCameraRenderingHandler;
    private RenderTexture? _renderTexture;
    private CommandBuffer? _captureCommand;
    private Camera? _builtInAttachedCamera;
    private bool _enabled;
    private bool _viewerVisible = true;
    private bool _bridgeReady;
    private int _selectedSourceCameraInstanceId;
    private int _effectiveSourceCameraInstanceId;
    private string _effectiveSourceCameraName = string.Empty;
    private int _renderWidth = DefaultRenderWidth;
    private int _renderHeight = DefaultRenderHeight;
    private int _renderGeneration;
    private float _idleFps = DefaultIdleFps;
    private float _interactiveFps = DefaultInteractiveFps;
    private float _nextCaptureAt;
    private IntPtr _renderEvent;
    private int _copyEventId;
    private int _dxgiFormat;
    private ulong _adapterLuid;
    private int _nativeBridgeAbiVersion;
    private string _adapterName = string.Empty;
    private string _sharedName = string.Empty;
    private string _status = "Direct source Camera capture is disabled.";
    private double _lastRenderMs;
    private double _averageRenderMs;
    private double _maxRenderMs;
    private long _renderSamples;
    private float _sceneFpsWindowStart;
    private int _sceneFpsWindowFrames;
    private double _sceneFps;

    public SourceCameraCaptureController()
    {
        _managedEndCameraRenderingHandler = OnEndCameraRendering;
        _endCameraRenderingHandler =
            (Il2CppSystem.Action<ScriptableRenderContext, Camera>)_managedEndCameraRenderingHandler;
        RenderPipelineManager.endCameraRendering += _endCameraRenderingHandler;
    }

    public bool Enabled => _enabled;

    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled)
        {
            return;
        }

        _enabled = enabled;
        if (_enabled)
        {
            EnsureRenderTexture();
            TryInitializeBridge();
            RefreshSourceCamera(force: true);
            _status = _bridgeReady
                ? BuildReadyStatus()
                : _status;
            return;
        }

        DetachBuiltInCommandBuffer();
        ResetBridge();
        ReleaseRenderTexture();
        _effectiveSourceCameraInstanceId = 0;
        _effectiveSourceCameraName = string.Empty;
        _sceneFps = 0d;
        _status = "Direct source Camera capture is disabled.";
    }

    public void Apply(ViewerCommand command)
    {
        switch (command.Kind)
        {
            case ViewerCommandKind.CameraSource:
                _selectedSourceCameraInstanceId = command.InstanceId;
                RefreshSourceCamera(force: true);
                break;
            case ViewerCommandKind.CameraVisibility:
                _viewerVisible = command.Flag;
                if (!_viewerVisible)
                {
                    _sceneFps = 0d;
                }
                break;
            case ViewerCommandKind.CameraStreamSettings:
                _idleFps = Mathf.Clamp(SanitizeFinite(command.X, DefaultIdleFps), MinStreamFps, MaxStreamFps);
                _interactiveFps = Mathf.Clamp(SanitizeFinite(command.Y, DefaultInteractiveFps), MinStreamFps, MaxStreamFps);
                var width = Mathf.Clamp((int)command.Z, MinRenderDimension, MaxRenderDimension);
                var height = Mathf.Clamp((int)command.Value, MinRenderDimension, MaxRenderDimension);
                if (_enabled && (width != _renderWidth || height != _renderHeight))
                {
                    RecreateRenderTexture(width, height);
                }
                else
                {
                    _renderWidth = width;
                    _renderHeight = height;
                }
                break;
            case ViewerCommandKind.CameraRecover:
                if (_enabled)
                {
                    RecreateRenderTexture(_renderWidth, _renderHeight);
                }
                break;
        }
    }

    public void Tick()
    {
        if (!_enabled)
        {
            return;
        }

        RefreshSourceCamera(force: false);
    }

    public RenderTargetInfo GetRenderTargetInfo()
    {
        var source = ResolveSourceCamera();
        return new RenderTargetInfo
        {
            Available = _bridgeReady,
            SharedName = _sharedName,
            Width = _renderWidth,
            Height = _renderHeight,
            DxgiFormat = _dxgiFormat,
            AdapterLuid = _adapterLuid,
            AdapterName = _adapterName,
            NativeBridgeAbiVersion = _nativeBridgeAbiVersion,
            Orthographic = source?.orthographic ?? false,
            FieldOfView = source?.fieldOfView ?? 60f,
            NearClipPlane = source?.nearClipPlane ?? 0.001f,
            FarClipPlane = source?.farClipPlane ?? 10000f,
            OrthographicSize = source?.orthographicSize ?? 5f,
            MoveSpeed = 0f,
            IdleFps = _idleFps,
            InteractiveFps = _interactiveFps,
            Cameras = BuildCameraList(),
            SelectedCameraInstanceId = _selectedSourceCameraInstanceId,
            SourceCameraInstanceId = _effectiveSourceCameraInstanceId,
            SourceCameraName = _effectiveSourceCameraName,
            CullingMode = SceneCullingMode.MainCamera,
            CullingMask = source?.cullingMask ?? -1,
            Status = _status
        };
    }

    public void PopulatePerformance(PerformanceInfo performance)
    {
        performance.SceneFps = _sceneFps;
        performance.SceneRenderMs = _lastRenderMs;
        performance.SceneRenderAverageMs = _averageRenderMs;
        performance.SceneRenderMaxMs = _maxRenderMs;
    }

    private void OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
    {
        if (!_enabled || !_viewerVisible || !_bridgeReady || camera is null ||
            camera.GetInstanceID() != _effectiveSourceCameraInstanceId)
        {
            return;
        }

        var now = Time.unscaledTime;
        var fps = Mathf.Max(MinStreamFps, _idleFps);
        if (now < _nextCaptureAt)
        {
            return;
        }
        _nextCaptureAt = now + 1f / fps;

        var command = EnsureCaptureCommand();
        if (command is null)
        {
            return;
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            context.ExecuteCommandBuffer(command);
            RecordRenderTiming(ElapsedMilliseconds(started));
            RecordRenderFrame(now);
            _status = BuildReadyStatus();
        }
        catch (Exception ex)
        {
            _status = $"Direct source Camera capture failed: {ex.Message}";
        }
    }

    private void RefreshSourceCamera(bool force)
    {
        var source = ResolveSourceCamera();
        var instanceId = source is null ? 0 : source.GetInstanceID();
        if (!force && instanceId == _effectiveSourceCameraInstanceId)
        {
            return;
        }

        DetachBuiltInCommandBuffer();
        _effectiveSourceCameraInstanceId = instanceId;
        _effectiveSourceCameraName = source?.name ?? string.Empty;
        _nextCaptureAt = 0f;

        if (!_enabled)
        {
            return;
        }

        if (source is null)
        {
            _status = _selectedSourceCameraInstanceId == 0
                ? "Direct capture is waiting for a usable game Camera."
                : $"Selected Camera {_selectedSourceCameraInstanceId} is unavailable for direct capture.";
            return;
        }

        if (RenderPipelineManager.currentPipeline is null)
        {
            AttachBuiltInCommandBuffer(source);
        }

        _status = BuildReadyStatus();
    }

    private Camera? ResolveSourceCamera()
    {
        var cameras = Camera.allCameras;
        if (_selectedSourceCameraInstanceId != 0)
        {
            for (var index = 0; index < cameras.Length; index++)
            {
                var candidate = cameras[index];
                if (IsUsableSourceCamera(candidate) &&
                    candidate.GetInstanceID() == _selectedSourceCameraInstanceId)
                {
                    return candidate;
                }
            }
        }

        var main = Camera.main;
        if (IsUsableSourceCamera(main))
        {
            return main;
        }

        Camera? bestScreen = null;
        Camera? bestAny = null;
        for (var index = 0; index < cameras.Length; index++)
        {
            var candidate = cameras[index];
            if (!IsUsableSourceCamera(candidate))
            {
                continue;
            }

            if (bestAny is null || candidate.depth > bestAny.depth)
            {
                bestAny = candidate;
            }
            if (candidate.targetTexture is null &&
                (bestScreen is null || candidate.depth > bestScreen.depth))
            {
                bestScreen = candidate;
            }
        }

        return bestScreen ?? bestAny;
    }

    private static bool IsUsableSourceCamera(Camera? camera) =>
        camera is not null &&
        camera.gameObject is not null &&
        camera.gameObject.activeInHierarchy &&
        !string.Equals(camera.gameObject.name, ViewerCameraObjectName, StringComparison.Ordinal);

    private CameraInfo[] BuildCameraList()
    {
        var result = new List<CameraInfo>();
        var cameras = Camera.allCameras;
        for (var index = 0; index < cameras.Length; index++)
        {
            var camera = cameras[index];
            if (!IsUsableSourceCamera(camera))
            {
                continue;
            }

            result.Add(new CameraInfo
            {
                InstanceId = camera.GetInstanceID(),
                Name = camera.name ?? string.Empty,
                Depth = camera.depth,
                Enabled = camera.enabled,
                ActiveInHierarchy = camera.gameObject.activeInHierarchy,
                HasTargetTexture = camera.targetTexture is not null,
                TargetDisplay = camera.targetDisplay,
                Orthographic = camera.orthographic
            });
        }

        result.Sort((left, right) =>
        {
            var screenCompare = left.HasTargetTexture.CompareTo(right.HasTargetTexture);
            if (screenCompare != 0)
            {
                return screenCompare;
            }
            var depthCompare = right.Depth.CompareTo(left.Depth);
            return depthCompare != 0
                ? depthCompare
                : string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
        });
        return result.ToArray();
    }

    private void AttachBuiltInCommandBuffer(Camera camera)
    {
        var command = EnsureCaptureCommand();
        if (command is null)
        {
            return;
        }

        try
        {
            camera.AddCommandBuffer(CameraEvent.AfterEverything, command);
            _builtInAttachedCamera = camera;
        }
        catch (Exception ex)
        {
            _status = $"Could not attach direct capture to source Camera: {ex.Message}";
        }
    }

    private void DetachBuiltInCommandBuffer()
    {
        if (_builtInAttachedCamera is null || _captureCommand is null)
        {
            _builtInAttachedCamera = null;
            return;
        }

        try
        {
            _builtInAttachedCamera.RemoveCommandBuffer(CameraEvent.AfterEverything, _captureCommand);
        }
        catch
        {
        }
        _builtInAttachedCamera = null;
    }

    private CommandBuffer? EnsureCaptureCommand()
    {
        if (_renderTexture is null || !_bridgeReady || _renderEvent == IntPtr.Zero)
        {
            return null;
        }

        if (_captureCommand is null)
        {
            _captureCommand = new CommandBuffer { name = "U3DViewer Direct Source Camera Capture" };
        }

        _captureCommand.Clear();
        _captureCommand.Blit(
            BuiltinRenderTextureType.CameraTarget,
            new RenderTargetIdentifier(_renderTexture));
        _captureCommand.IssuePluginEvent(_renderEvent, _copyEventId);
        return _captureCommand;
    }

    private void EnsureRenderTexture()
    {
        if (_renderTexture is not null)
        {
            return;
        }

        _renderTexture = new RenderTexture(_renderWidth, _renderHeight, 0, RenderTextureFormat.ARGB32)
        {
            name = "__U3DViewerSourceCaptureTexture"
        };
        _renderTexture.Create();
    }

    private void RecreateRenderTexture(int width, int height)
    {
        DetachBuiltInCommandBuffer();
        ResetBridge();
        ReleaseRenderTexture();
        _renderWidth = width;
        _renderHeight = height;
        _renderGeneration++;
        EnsureRenderTexture();
        TryInitializeBridge();
        RefreshSourceCamera(force: true);
    }

    private void TryInitializeBridge()
    {
        if (!_enabled || _renderTexture is null)
        {
            return;
        }

        if (!string.Equals(SystemInfo.graphicsDeviceType.ToString(), "Direct3D11", StringComparison.Ordinal))
        {
            _bridgeReady = false;
            _status = $"Direct source capture requires Direct3D11, current API is {SystemInfo.graphicsDeviceType}.";
            return;
        }

        try
        {
            _nativeBridgeAbiVersion = NativeBridge.U3DViewer_GetAbiVersion();
            if (_nativeBridgeAbiVersion != NativeBridgeProtocol.AbiVersion)
            {
                _bridgeReady = false;
                _status = $"NativeBridge ABI mismatch: game={_nativeBridgeAbiVersion}, viewer={NativeBridgeProtocol.AbiVersion}.";
                return;
            }

            var nativeTexture = _renderTexture.GetNativeTexturePtr();
            if (nativeTexture == IntPtr.Zero)
            {
                _bridgeReady = false;
                _status = "Direct capture RenderTexture returned a null native texture pointer.";
                return;
            }

            _sharedName = $"U3DViewer.Scene.Source.{Process.GetCurrentProcess().Id}.{_renderGeneration}";
            if (NativeBridge.U3DViewer_SetSourceTexture(nativeTexture, _sharedName) == 0)
            {
                _bridgeReady = false;
                _status = $"NativeBridge rejected direct capture texture (HRESULT 0x{NativeBridge.U3DViewer_GetLastError():X8}).";
                return;
            }

            _renderEvent = NativeBridge.U3DViewer_GetRenderEventFunc();
            _copyEventId = NativeBridge.U3DViewer_GetCopyEventId();
            _dxgiFormat = NativeBridge.U3DViewer_GetSourceDxgiFormat();
            _adapterLuid = NativeBridge.U3DViewer_GetSourceAdapterLuid();
            _adapterName = SystemInfo.graphicsDeviceName ?? string.Empty;
            _bridgeReady = _renderEvent != IntPtr.Zero;
            _status = _bridgeReady
                ? BuildReadyStatus()
                : "NativeBridge did not return a render event callback for direct capture.";
        }
        catch (Exception ex)
        {
            _bridgeReady = false;
            _status = $"Direct source Camera transport initialization failed: {ex.Message}";
        }
    }

    private string BuildReadyStatus()
    {
        var pipeline = RenderPipelineManager.currentPipeline;
        var pipelineName = pipeline is null ? "Built-in" : pipeline.GetType().FullName ?? pipeline.GetType().Name;
        var source = string.IsNullOrWhiteSpace(_effectiveSourceCameraName)
            ? "no source Camera"
            : $"{_effectiveSourceCameraName} [id {_effectiveSourceCameraInstanceId}]";
        return $"Direct source Camera capture active: {source}; pipeline {pipelineName}.";
    }

    private void ResetBridge()
    {
        _bridgeReady = false;
        _renderEvent = IntPtr.Zero;
        try
        {
            NativeBridge.U3DViewer_Reset();
        }
        catch
        {
        }
    }

    private void ReleaseRenderTexture()
    {
        if (_renderTexture is not null)
        {
            _renderTexture.Release();
            UnityEngine.Object.Destroy(_renderTexture);
            _renderTexture = null;
        }

        if (_captureCommand is not null)
        {
            _captureCommand.Release();
            _captureCommand = null;
        }
    }

    private void RecordRenderTiming(double milliseconds)
    {
        _lastRenderMs = milliseconds;
        _renderSamples++;
        _averageRenderMs += (milliseconds - _averageRenderMs) / _renderSamples;
        _maxRenderMs = Math.Max(_maxRenderMs, milliseconds);
    }

    private void RecordRenderFrame(float now)
    {
        if (_sceneFpsWindowStart <= 0f)
        {
            _sceneFpsWindowStart = now;
            _sceneFpsWindowFrames = 0;
        }

        _sceneFpsWindowFrames++;
        var elapsed = now - _sceneFpsWindowStart;
        if (elapsed < 0.5f)
        {
            return;
        }

        _sceneFps = elapsed > 0f ? _sceneFpsWindowFrames / elapsed : 0d;
        _sceneFpsWindowStart = now;
        _sceneFpsWindowFrames = 0;
    }

    private static double ElapsedMilliseconds(long started) =>
        (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;

    private static float SanitizeFinite(float value, float fallback) =>
        float.IsNaN(value) || float.IsInfinity(value) ? fallback : value;

    public void Dispose()
    {
        RenderPipelineManager.endCameraRendering -= _endCameraRenderingHandler;
        DetachBuiltInCommandBuffer();
        ResetBridge();
        ReleaseRenderTexture();
        _enabled = false;
    }
}
