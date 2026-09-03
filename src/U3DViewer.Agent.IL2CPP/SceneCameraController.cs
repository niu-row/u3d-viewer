using System.Collections.Generic;
using System.Diagnostics;
using U3DViewer.Protocol;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace U3DViewer.Agent.IL2CPP;

internal sealed class SceneCameraController : IDisposable
{
    private const int DefaultRenderWidth = 1280;
    private const int DefaultRenderHeight = 720;
    private const float DefaultIdleFps = 15f;
    private const float DefaultInteractiveFps = 30f;
    private const float InteractiveHoldSeconds = 0.2f;
    private const float FollowSourceLookupInterval = 1f;
    private const float SourceCameraRefreshInterval = 1f;
    private const float DefaultPerspectiveFov = 60f;
    private const float MinPerspectiveNear = 0.001f;
    private const float MinPerspectiveFov = 1f;
    private const float MaxPerspectiveFov = 179f;
    private const float DefaultPerspectiveFar = 10000f;
    private const float MinOrthographicSize = 0.001f;
    private const float MinClipSeparation = 0.0001f;
    private const float MinStreamFps = 1f;
    private const float MaxStreamFps = 120f;
    private const int MinRenderDimension = 64;
    private const int MaxRenderDimension = 4096;

    private GameObject? _cameraObject;
    private Camera? _camera;
    private Camera? _followSourceCamera;
    private RenderTexture? _renderTexture;
    private RenderTexture? _transportTexture;
    private float _moveSpeed = 10f;
    private float _idleFps = DefaultIdleFps;
    private float _interactiveFps = DefaultInteractiveFps;
    private int _renderWidth = DefaultRenderWidth;
    private int _renderHeight = DefaultRenderHeight;
    private int _renderGeneration;
    private float _nextRenderAt;
    private float _interactiveUntil;
    private float _nextFollowSourceLookupAt;
    private float _nextSourceCameraRefreshAt;
    private bool _followPosition;
    private bool _followRotation;
    private bool _viewerVisible = true;
    private bool _bridgeReady;
    private IntPtr _renderEvent;
    private int _copyEventId;
    private int _dxgiFormat;
    private ulong _adapterLuid;
    private int _nativeBridgeAbiVersion;
    private int _selectedSourceCameraInstanceId;
    private int _effectiveSourceCameraInstanceId;
    private string _effectiveSourceCameraName = string.Empty;
    private string _adapterName = string.Empty;
    private string _sharedName = string.Empty;
    private string _sourceProjectionInfo = "Source Camera: unavailable";
    private float _preferredOrthographicSize = 5f;
    private string _renderStatus = "Scene Camera has not initialized yet.";
    private double _lastRenderMs;
    private double _averageRenderMs;
    private double _maxRenderMs;
    private long _renderSamples;
    private float _sceneFpsWindowStart;
    private int _sceneFpsWindowFrames;
    private double _sceneFps;

    public void Apply(ViewerCommand command)
    {
        EnsureCamera();
        var camera = _camera!;
        var transform = camera.transform;

        switch (command.Kind)
        {
            case ViewerCommandKind.CameraMove:
            {
                var deltaSeconds = Mathf.Clamp(command.Value, 0f, 0.25f);
                var movement =
                    transform.forward * command.X +
                    transform.right * command.Y +
                    transform.up * command.Z;
                transform.position += movement * (_moveSpeed * deltaSeconds);
                BoostInteractiveRender();
                break;
            }
            case ViewerCommandKind.CameraLook:
            {
                var euler = transform.eulerAngles;
                var pitch = NormalizePitch(euler.x + command.Y);
                var yaw = euler.y + command.X;
                transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
                BoostInteractiveRender();
                break;
            }
            case ViewerCommandKind.CameraSpeed:
                _moveSpeed = Mathf.Clamp(command.Value, 0.1f, 1000f);
                break;
            case ViewerCommandKind.CameraProjection:
                ApplyProjection(camera, command.Flag);
                BoostInteractiveRender();
                break;
            case ViewerCommandKind.CameraLens:
                ApplyLens(camera, command.X, command.Y, command.Z, command.Value);
                BoostInteractiveRender();
                break;
            case ViewerCommandKind.CameraStreamSettings:
                ApplyStreamSettings(command.X, command.Y, (int)command.Z, (int)command.Value);
                break;
            case ViewerCommandKind.CameraCullingMask:
                BoostInteractiveRender();
                break;
            case ViewerCommandKind.CameraFollowTransform:
                _followPosition = command.Flag;
                _followRotation = command.Flag2;
                _followSourceCamera = null;
                _nextFollowSourceLookupAt = 0f;
                ApplyFollowTransform(forceLookup: true);
                BoostInteractiveRender();
                break;
            case ViewerCommandKind.CameraSource:
                _selectedSourceCameraInstanceId = command.InstanceId;
                _followSourceCamera = null;
                _nextFollowSourceLookupAt = 0f;
                _nextSourceCameraRefreshAt = 0f;
                CopyFromGameCamera();
                BoostInteractiveRender();
                break;
            case ViewerCommandKind.CameraReset:
                CopyFromGameCamera();
                BoostInteractiveRender();
                break;
            case ViewerCommandKind.CameraRecover:
                RecoverTransport();
                break;
            case ViewerCommandKind.CameraVisibility:
                _viewerVisible = command.Flag;
                if (_viewerVisible)
                {
                    BoostInteractiveRender();
                }
                else
                {
                    ResetSceneFps(Time.unscaledTime);
                }
                break;
            case ViewerCommandKind.CameraFocus:
            {
                var target = FindGameObject(command.InstanceId);
                if (target is not null)
                {
                    Focus(target.transform.position);
                    BoostInteractiveRender();
                }
                break;
            }
        }
    }

    public void TickRender()
    {
        EnsureCamera();
        var now = Time.unscaledTime;
        RefreshSourceCameraIfNeeded(now);
        if (!_viewerVisible)
        {
            ResetSceneFps(now);
            return;
        }

        if (!_bridgeReady || _camera is null || _renderTexture is null || _transportTexture is null || now < _nextRenderAt)
        {
            return;
        }

        ApplyFollowTransform();

        var fps = now < _interactiveUntil ? _interactiveFps : _idleFps;
        _nextRenderAt = now + 1f / Mathf.Max(MinStreamFps, fps);

        var started = Stopwatch.GetTimestamp();
        try
        {
            _camera.Render();
            Graphics.Blit(_renderTexture, _transportTexture);
            GL.IssuePluginEvent(_renderEvent, _copyEventId);
            RecordRenderTiming(ElapsedMilliseconds(started));
            RecordRenderFrame(now);
        }
        catch (Exception ex)
        {
            _bridgeReady = false;
            _renderStatus = $"Scene render failed: {ex.Message}";
        }
    }

    public RenderTargetInfo GetRenderTargetInfo()
    {
        EnsureCamera();
        var camera = _camera;
        var projection = camera is null
            ? "Scene Camera unavailable"
            : camera.orthographic
                ? $"Scene Orthographic size={camera.orthographicSize:0.###}, near={camera.nearClipPlane:0.###}, far={camera.farClipPlane:0.###}"
                : $"Scene Perspective FOV={camera.fieldOfView:0.###}, near={camera.nearClipPlane:0.###}, far={camera.farClipPlane:0.###}";

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
            Orthographic = camera?.orthographic ?? false,
            FieldOfView = camera?.fieldOfView ?? DefaultPerspectiveFov,
            NearClipPlane = camera?.nearClipPlane ?? MinPerspectiveNear,
            FarClipPlane = camera?.farClipPlane ?? DefaultPerspectiveFar,
            OrthographicSize = camera?.orthographicSize ?? _preferredOrthographicSize,
            MoveSpeed = _moveSpeed,
            IdleFps = _idleFps,
            InteractiveFps = _interactiveFps,
            Cameras = BuildCameraList(),
            SelectedCameraInstanceId = _selectedSourceCameraInstanceId,
            SourceCameraInstanceId = _effectiveSourceCameraInstanceId,
            SourceCameraName = _effectiveSourceCameraName,
            Status = $"{_renderStatus} {_sourceProjectionInfo} -> {projection}."
        };
    }

    public void PopulatePerformance(PerformanceInfo performance)
    {
        performance.SceneFps = _sceneFps;
        performance.SceneRenderMs = _lastRenderMs;
        performance.SceneRenderAverageMs = _averageRenderMs;
        performance.SceneRenderMaxMs = _maxRenderMs;
    }

    private void EnsureCamera()
    {
        if (_camera is not null)
        {
            return;
        }

        _cameraObject = new GameObject("__U3DViewerCamera");
        UnityEngine.Object.DontDestroyOnLoad(_cameraObject);

        _camera = _cameraObject.AddComponent<Camera>();
        _camera.enabled = false;
        _camera.orthographic = false;
        _camera.nearClipPlane = MinPerspectiveNear;
        _camera.farClipPlane = DefaultPerspectiveFar;
        _camera.fieldOfView = DefaultPerspectiveFov;

        CreateRenderTextures(_renderWidth, _renderHeight);
        CopyFromGameCamera();
        TryInitializeBridge();
    }

    private void ApplyStreamSettings(float idleFps, float interactiveFps, int width, int height)
    {
        _idleFps = Mathf.Clamp(SanitizeFinite(idleFps, DefaultIdleFps), MinStreamFps, MaxStreamFps);
        _interactiveFps = Mathf.Clamp(SanitizeFinite(interactiveFps, DefaultInteractiveFps), MinStreamFps, MaxStreamFps);

        width = Mathf.Clamp(width, MinRenderDimension, MaxRenderDimension);
        height = Mathf.Clamp(height, MinRenderDimension, MaxRenderDimension);

        if (width != _renderWidth || height != _renderHeight)
        {
            RecreateRenderTexture(width, height);
        }

        BoostInteractiveRender();
    }

    private void RecoverTransport()
    {
        RecreateRenderTexture(_renderWidth, _renderHeight);
        BoostInteractiveRender();
    }

    private void RecreateRenderTexture(int width, int height)
    {
        if (_camera is null)
        {
            _renderWidth = width;
            _renderHeight = height;
            return;
        }

        _bridgeReady = false;
        _renderEvent = IntPtr.Zero;

        try
        {
            SceneTransportCoordinator.ResetIfOwner(SceneTransportOwner.FreeCamera);
        }
        catch
        {
            // The bridge may not have initialized yet.
        }

        _camera.targetTexture = null;
        ReleaseRenderTextures();
        _renderWidth = width;
        _renderHeight = height;
        _renderGeneration++;
        CreateRenderTextures(width, height);
        TryInitializeBridge();
    }

    private void CreateRenderTextures(int width, int height)
    {
        _renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
        {
            name = "__U3DViewerRenderTexture"
        };
        _renderTexture.Create();

        _transportTexture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
        {
            name = "__U3DViewerTransportTexture"
        };
        _transportTexture.Create();

        if (_camera is not null)
        {
            _camera.targetTexture = _renderTexture;
        }
    }

    private void ReleaseRenderTextures()
    {
        if (_renderTexture is not null)
        {
            _renderTexture.Release();
            UnityEngine.Object.Destroy(_renderTexture);
            _renderTexture = null;
        }

        if (_transportTexture is not null)
        {
            _transportTexture.Release();
            UnityEngine.Object.Destroy(_transportTexture);
            _transportTexture = null;
        }
    }

    private void TryInitializeBridge()
    {
        if (_transportTexture is null)
        {
            return;
        }

        if (!string.Equals(SystemInfo.graphicsDeviceType.ToString(), "Direct3D11", StringComparison.Ordinal))
        {
            _renderStatus = $"Unsupported graphics API: {SystemInfo.graphicsDeviceType}. M4 currently requires Direct3D11.";
            return;
        }

        try
        {
            _nativeBridgeAbiVersion = NativeBridge.U3DViewer_GetAbiVersion();
            if (_nativeBridgeAbiVersion != NativeBridgeProtocol.AbiVersion)
            {
                _bridgeReady = false;
                _renderStatus = $"NativeBridge ABI mismatch: game={_nativeBridgeAbiVersion}, viewer expects={NativeBridgeProtocol.AbiVersion}. Redeploy and restart the game.";
                return;
            }

            _sharedName = $"U3DViewer.Scene.{Process.GetCurrentProcess().Id}.{_renderGeneration}";
            var nativeTexture = _transportTexture.GetNativeTexturePtr();
            if (nativeTexture == IntPtr.Zero)
            {
                _renderStatus = "Transport RenderTexture returned a null native texture pointer.";
                return;
            }

            if (NativeBridge.U3DViewer_SetSourceTexture(nativeTexture, _sharedName) == 0)
            {
                _renderStatus = $"NativeBridge rejected the transport RenderTexture (HRESULT 0x{NativeBridge.U3DViewer_GetLastError():X8}).";
                return;
            }

            _renderEvent = NativeBridge.U3DViewer_GetRenderEventFunc();
            _copyEventId = NativeBridge.U3DViewer_GetCopyEventId();
            _dxgiFormat = NativeBridge.U3DViewer_GetSourceDxgiFormat();
            _adapterLuid = NativeBridge.U3DViewer_GetSourceAdapterLuid();
            _adapterName = SystemInfo.graphicsDeviceName ?? string.Empty;
            _bridgeReady = _renderEvent != IntPtr.Zero;
            _renderStatus = _bridgeReady
                ? $"D3D11 shared Scene transport target is ready on {DisplayAdapterName(_adapterName)}."
                : "NativeBridge did not return a render event callback.";
        }
        catch (DllNotFoundException)
        {
            _nativeBridgeAbiVersion = 0;
            _renderStatus = "U3DViewer.NativeBridge.dll was not found. Copy the x64 DLL next to the target game executable.";
        }
        catch (EntryPointNotFoundException)
        {
            _nativeBridgeAbiVersion = 0;
            _bridgeReady = false;
            _renderStatus = $"NativeBridge ABI is outdated. Viewer expects ABI {NativeBridgeProtocol.AbiVersion}. Redeploy and restart the game.";
        }
        catch (Exception ex)
        {
            _renderStatus = $"NativeBridge initialization failed: {ex.Message}";
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

    private void ResetSceneFps(float now)
    {
        _sceneFps = 0d;
        _sceneFpsWindowStart = now;
        _sceneFpsWindowFrames = 0;
    }

    private void RefreshSourceCameraIfNeeded(float now)
    {
        if (now < _nextSourceCameraRefreshAt)
        {
            return;
        }

        _nextSourceCameraRefreshAt = now + SourceCameraRefreshInterval;
        var source = ResolveSourceCamera();
        var instanceId = source is null ? 0 : source.GetInstanceID();
        if (instanceId != _effectiveSourceCameraInstanceId)
        {
            CopyFromGameCamera();
        }
    }

    private void ApplyFollowTransform(bool forceLookup = false)
    {
        var camera = _camera;
        if (camera is null || (!_followPosition && !_followRotation))
        {
            return;
        }

        var now = Time.unscaledTime;
        if (forceLookup || _followSourceCamera is null || now >= _nextFollowSourceLookupAt)
        {
            _nextFollowSourceLookupAt = now + FollowSourceLookupInterval;
            _followSourceCamera = ResolveSourceCamera();
        }

        var followSource = _followSourceCamera;
        if (followSource is null || followSource == camera)
        {
            return;
        }

        var transform = camera.transform;
        var sourceTransform = followSource.transform;
        if (_followPosition)
        {
            transform.position = sourceTransform.position;
        }
        if (_followRotation)
        {
            transform.rotation = sourceTransform.rotation;
        }
    }

    private Camera? ResolveSourceCamera()
    {
        var cameras = Camera.allCameras;
        if (_selectedSourceCameraInstanceId != 0)
        {
            for (var i = 0; i < cameras.Length; i++)
            {
                var candidate = cameras[i];
                if (IsUsableSourceCamera(candidate) && candidate.GetInstanceID() == _selectedSourceCameraInstanceId)
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

        Camera? bestScreenCamera = null;
        Camera? bestAnyCamera = null;
        for (var i = 0; i < cameras.Length; i++)
        {
            var candidate = cameras[i];
            if (!IsUsableSourceCamera(candidate))
            {
                continue;
            }

            if (bestAnyCamera is null || candidate.depth > bestAnyCamera.depth)
            {
                bestAnyCamera = candidate;
            }

            if (candidate.targetTexture is null &&
                (bestScreenCamera is null || candidate.depth > bestScreenCamera.depth))
            {
                bestScreenCamera = candidate;
            }
        }

        return bestScreenCamera ?? bestAnyCamera;
    }

    private bool IsUsableSourceCamera(Camera? candidate) =>
        candidate is not null && candidate != _camera && candidate.gameObject.activeInHierarchy;

    private CameraInfo[] BuildCameraList()
    {
        var result = new List<CameraInfo>();
        var cameras = Camera.allCameras;
        for (var i = 0; i < cameras.Length; i++)
        {
            var candidate = cameras[i];
            if (!IsUsableSourceCamera(candidate))
            {
                continue;
            }

            result.Add(new CameraInfo
            {
                InstanceId = candidate.GetInstanceID(),
                Name = candidate.name ?? string.Empty,
                Depth = candidate.depth,
                Enabled = candidate.enabled,
                ActiveInHierarchy = candidate.gameObject.activeInHierarchy,
                HasTargetTexture = candidate.targetTexture is not null,
                TargetDisplay = candidate.targetDisplay,
                Orthographic = candidate.orthographic
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

    private static double ElapsedMilliseconds(long started) =>
        (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;

    private void BoostInteractiveRender()
    {
        var now = Time.unscaledTime;
        _interactiveUntil = now + InteractiveHoldSeconds;
        _nextRenderAt = Mathf.Min(_nextRenderAt, now);
    }

    private static string DisplayAdapterName(string value) =>
        string.IsNullOrWhiteSpace(value) ? "an unknown GPU" : value;

    private void CopyFromGameCamera()
    {
        var camera = _camera;
        if (camera is null)
        {
            return;
        }

        var source = ResolveSourceCamera();
        if (source is null || source == camera)
        {
            _followSourceCamera = null;
            _effectiveSourceCameraInstanceId = 0;
            _effectiveSourceCameraName = string.Empty;
            _nextFollowSourceLookupAt = 0f;
            _sourceProjectionInfo = _selectedSourceCameraInstanceId == 0
                ? "Source Camera: unavailable"
                : $"Selected Camera {_selectedSourceCameraInstanceId}: unavailable; automatic fallback also unavailable";
            _preferredOrthographicSize = 5f;
            camera.transform.position = new Vector3(0f, 2f, -5f);
            camera.transform.rotation = Quaternion.identity;
            camera.cullingMask = -1;
            camera.fieldOfView = DefaultPerspectiveFov;
            camera.nearClipPlane = MinPerspectiveNear;
            camera.farClipPlane = DefaultPerspectiveFar;
            camera.orthographicSize = _preferredOrthographicSize;
            camera.orthographic = false;
            return;
        }

        _followSourceCamera = source;
        _effectiveSourceCameraInstanceId = source.GetInstanceID();
        _effectiveSourceCameraName = source.name ?? string.Empty;
        _nextFollowSourceLookupAt = Time.unscaledTime + FollowSourceLookupInterval;

        var viewerTarget = _renderTexture;
        camera.CopyFrom(source);
        camera.enabled = false;
        camera.targetTexture = viewerTarget;
        camera.transform.position = source.transform.position;
        camera.transform.rotation = source.transform.rotation;

        _preferredOrthographicSize = SanitizeOrthographicSize(source.orthographicSize);
        camera.orthographicSize = _preferredOrthographicSize;

        if (source.orthographic)
        {
            camera.nearClipPlane = source.nearClipPlane;
            camera.farClipPlane = SanitizeFar(source.farClipPlane, camera.nearClipPlane);
            _sourceProjectionInfo =
                $"Source {source.name} [id {_effectiveSourceCameraInstanceId}] Orthographic size={source.orthographicSize:0.###}, near={source.nearClipPlane:0.###}, far={source.farClipPlane:0.###}";
        }
        else
        {
            camera.fieldOfView = SanitizeFov(source.fieldOfView);
            camera.nearClipPlane = SanitizeNear(source.nearClipPlane);
            camera.farClipPlane = SanitizeFar(source.farClipPlane, camera.nearClipPlane);
            _sourceProjectionInfo =
                $"Source {source.name} [id {_effectiveSourceCameraInstanceId}] Perspective FOV={source.fieldOfView:0.###}, near={source.nearClipPlane:0.###}, far={source.farClipPlane:0.###}";
        }
    }

    private void ApplyProjection(Camera camera, bool orthographic)
    {
        if (orthographic)
        {
            camera.orthographicSize = SanitizeOrthographicSize(_preferredOrthographicSize);
            camera.orthographic = true;
            return;
        }

        camera.fieldOfView = SanitizeFov(camera.fieldOfView);
        camera.nearClipPlane = SanitizeNear(camera.nearClipPlane);
        camera.farClipPlane = SanitizeFar(camera.farClipPlane, camera.nearClipPlane);
        camera.orthographic = false;
    }

    private void ApplyLens(Camera camera, float fieldOfView, float nearClip, float farClip, float orthographicSize)
    {
        camera.fieldOfView = SanitizeFov(fieldOfView);
        _preferredOrthographicSize = SanitizeOrthographicSize(orthographicSize);
        camera.orthographicSize = _preferredOrthographicSize;

        if (camera.orthographic)
        {
            var near = SanitizeFinite(nearClip, camera.nearClipPlane);
            var far = SanitizeFinite(farClip, camera.farClipPlane);
            if (far <= near + MinClipSeparation)
            {
                far = near + 1f;
            }
            camera.nearClipPlane = near;
            camera.farClipPlane = far;
            return;
        }

        camera.nearClipPlane = SanitizeNear(nearClip);
        camera.farClipPlane = SanitizeFar(farClip, camera.nearClipPlane);
    }

    private static float SanitizeNear(float value) =>
        float.IsNaN(value) || float.IsInfinity(value) || value < MinPerspectiveNear
            ? MinPerspectiveNear
            : value;

    private static float SanitizeFar(float value, float near) =>
        float.IsNaN(value) || float.IsInfinity(value) || value <= near + MinClipSeparation
            ? Mathf.Max(DefaultPerspectiveFar, near + 1f)
            : value;

    private static float SanitizeFov(float value) =>
        float.IsNaN(value) || float.IsInfinity(value)
            ? DefaultPerspectiveFov
            : Mathf.Clamp(value, MinPerspectiveFov, MaxPerspectiveFov);

    private static float SanitizeOrthographicSize(float value) =>
        float.IsNaN(value) || float.IsInfinity(value)
            ? 5f
            : Mathf.Max(MinOrthographicSize, value);

    private static float SanitizeFinite(float value, float fallback) =>
        float.IsNaN(value) || float.IsInfinity(value) ? fallback : value;

    private void Focus(Vector3 target)
    {
        var transform = _camera!.transform;
        var offset = transform.position - target;
        if (offset.sqrMagnitude < 0.01f)
        {
            offset = new Vector3(0f, 2f, -5f);
        }

        var distance = Mathf.Clamp(offset.magnitude, 2f, 50f);
        transform.position = target + offset.normalized * distance;
        transform.LookAt(target);
    }

    private static float NormalizePitch(float value)
    {
        value %= 360f;
        if (value > 180f) value -= 360f;
        return Mathf.Clamp(value, -89f, 89f);
    }

    private static GameObject? FindGameObject(int instanceId)
    {
        for (var sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
        {
            var scene = SceneManager.GetSceneAt(sceneIndex);
            if (!scene.isLoaded)
            {
                continue;
            }

            GameObject[] roots;
            try
            {
                roots = scene.GetRootGameObjects();
            }
            catch
            {
                continue;
            }

            foreach (var root in roots)
            {
                var match = FindGameObject(root, instanceId);
                if (match is not null)
                {
                    return match;
                }
            }
        }

        return null;
    }

    private static GameObject? FindGameObject(GameObject gameObject, int instanceId)
    {
        if (gameObject.GetInstanceID() == instanceId)
        {
            return gameObject;
        }

        var transform = gameObject.transform;
        for (var index = 0; index < transform.childCount; index++)
        {
            var match = FindGameObject(transform.GetChild(index).gameObject, instanceId);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    public void Dispose()
    {
        try
        {
            SceneTransportCoordinator.ResetIfOwner(SceneTransportOwner.FreeCamera);
        }
        catch
        {
            // Bridge is optional until M4 deployment is complete.
        }

        if (_camera is not null)
        {
            _camera.targetTexture = null;
        }

        _followSourceCamera = null;
        ReleaseRenderTextures();

        if (_cameraObject is not null)
        {
            UnityEngine.Object.Destroy(_cameraObject);
            _cameraObject = null;
            _camera = null;
        }
    }
}
