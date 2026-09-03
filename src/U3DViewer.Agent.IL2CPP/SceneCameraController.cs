using System.Diagnostics;
using U3DViewer.Protocol;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace U3DViewer.Agent.IL2CPP;

internal sealed class SceneCameraController : IDisposable
{
    private const int RenderWidth = 1280;
    private const int RenderHeight = 720;
    private const float IdleRenderInterval = 1f / 15f;
    private const float InteractiveRenderInterval = 1f / 30f;
    private const float InteractiveHoldSeconds = 0.2f;
    private const float DefaultPerspectiveFov = 60f;
    private const float MinPerspectiveNear = 0.001f;
    private const float MinPerspectiveFov = 1f;
    private const float MaxPerspectiveFov = 179f;
    private const float DefaultPerspectiveFar = 10000f;
    private const float MinOrthographicSize = 0.001f;
    private const float MinClipSeparation = 0.0001f;

    private GameObject? _cameraObject;
    private Camera? _camera;
    private RenderTexture? _renderTexture;
    private float _moveSpeed = 10f;
    private float _nextRenderAt;
    private float _interactiveUntil;
    private bool _bridgeReady;
    private IntPtr _renderEvent;
    private int _copyEventId;
    private int _dxgiFormat;
    private ulong _adapterLuid;
    private string _adapterName = string.Empty;
    private string _sharedName = string.Empty;
    private string _sourceProjectionInfo = "Source Camera: unavailable";
    private float _preferredOrthographicSize = 5f;
    private string _renderStatus = "Scene Camera has not initialized yet.";

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
            case ViewerCommandKind.CameraReset:
                CopyFromGameCamera();
                BoostInteractiveRender();
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
        if (!_bridgeReady || _camera is null || now < _nextRenderAt)
        {
            return;
        }

        var interval = now < _interactiveUntil
            ? InteractiveRenderInterval
            : IdleRenderInterval;
        _nextRenderAt = now + interval;

        try
        {
            _camera.Render();
            GL.IssuePluginEvent(_renderEvent, _copyEventId);
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
            Width = RenderWidth,
            Height = RenderHeight,
            DxgiFormat = _dxgiFormat,
            AdapterLuid = _adapterLuid,
            AdapterName = _adapterName,
            Orthographic = camera?.orthographic ?? false,
            FieldOfView = camera?.fieldOfView ?? DefaultPerspectiveFov,
            NearClipPlane = camera?.nearClipPlane ?? MinPerspectiveNear,
            FarClipPlane = camera?.farClipPlane ?? DefaultPerspectiveFar,
            OrthographicSize = camera?.orthographicSize ?? _preferredOrthographicSize,
            Status = $"{_renderStatus} {_sourceProjectionInfo} -> {projection}."
        };
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

        _renderTexture = new RenderTexture(RenderWidth, RenderHeight, 24, RenderTextureFormat.ARGB32)
        {
            name = "__U3DViewerRenderTexture"
        };
        _renderTexture.Create();
        _camera.targetTexture = _renderTexture;

        CopyFromGameCamera();
        TryInitializeBridge();
    }

    private void TryInitializeBridge()
    {
        if (_renderTexture is null)
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
            _sharedName = $"U3DViewer.Scene.{Process.GetCurrentProcess().Id}";
            var nativeTexture = _renderTexture.GetNativeTexturePtr();
            if (nativeTexture == IntPtr.Zero)
            {
                _renderStatus = "RenderTexture returned a null native texture pointer.";
                return;
            }

            if (NativeBridge.U3DViewer_SetSourceTexture(nativeTexture, _sharedName) == 0)
            {
                _renderStatus = $"NativeBridge rejected the RenderTexture (HRESULT 0x{NativeBridge.U3DViewer_GetLastError():X8}).";
                return;
            }

            _renderEvent = NativeBridge.U3DViewer_GetRenderEventFunc();
            _copyEventId = NativeBridge.U3DViewer_GetCopyEventId();
            _dxgiFormat = NativeBridge.U3DViewer_GetSourceDxgiFormat();
            _adapterLuid = NativeBridge.U3DViewer_GetSourceAdapterLuid();
            _adapterName = SystemInfo.graphicsDeviceName ?? string.Empty;
            _bridgeReady = _renderEvent != IntPtr.Zero;
            _renderStatus = _bridgeReady
                ? $"D3D11 shared Scene render target is ready on {DisplayAdapterName(_adapterName)}."
                : "NativeBridge did not return a render event callback.";
        }
        catch (DllNotFoundException)
        {
            _renderStatus = "U3DViewer.NativeBridge.dll was not found. Copy the x64 DLL next to the target game executable.";
        }
        catch (EntryPointNotFoundException ex)
        {
            _renderStatus = $"NativeBridge API mismatch: {ex.Message}";
        }
        catch (Exception ex)
        {
            _renderStatus = $"NativeBridge initialization failed: {ex.Message}";
        }
    }

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

        var source = Camera.main;
        if (source is null || source == camera)
        {
            _sourceProjectionInfo = "Source Camera: unavailable";
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

        camera.transform.position = source.transform.position;
        camera.transform.rotation = source.transform.rotation;
        camera.clearFlags = source.clearFlags;
        camera.backgroundColor = source.backgroundColor;
        camera.cullingMask = source.cullingMask;

        _preferredOrthographicSize = SanitizeOrthographicSize(source.orthographicSize);
        camera.orthographicSize = _preferredOrthographicSize;

        if (source.orthographic)
        {
            camera.fieldOfView = DefaultPerspectiveFov;
            camera.nearClipPlane = MinPerspectiveNear;
            camera.farClipPlane = SanitizeFar(source.farClipPlane, camera.nearClipPlane);
            _sourceProjectionInfo =
                $"Source {source.name} Orthographic size={source.orthographicSize:0.###}, near={source.nearClipPlane:0.###}, far={source.farClipPlane:0.###}";
        }
        else
        {
            camera.fieldOfView = SanitizeFov(source.fieldOfView);
            camera.nearClipPlane = SanitizeNear(source.nearClipPlane);
            camera.farClipPlane = SanitizeFar(source.farClipPlane, camera.nearClipPlane);
            _sourceProjectionInfo =
                $"Source {source.name} Perspective FOV={source.fieldOfView:0.###}, near={source.nearClipPlane:0.###}, far={source.farClipPlane:0.###}";
        }

        camera.orthographic = false;
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
            foreach (var root in scene.GetRootGameObjects())
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
            NativeBridge.U3DViewer_Reset();
        }
        catch
        {
            // Bridge is optional until M4 deployment is complete.
        }

        if (_camera is not null)
        {
            _camera.targetTexture = null;
        }

        if (_renderTexture is not null)
        {
            _renderTexture.Release();
            UnityEngine.Object.Destroy(_renderTexture);
            _renderTexture = null;
        }

        if (_cameraObject is not null)
        {
            UnityEngine.Object.Destroy(_cameraObject);
            _cameraObject = null;
            _camera = null;
        }
    }
}
