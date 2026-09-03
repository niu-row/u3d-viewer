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

    private GameObject? _cameraObject;
    private Camera? _camera;
    private RenderTexture? _renderTexture;
    private float _moveSpeed = 10f;
    private float _nextRenderAt;
    private float _interactiveUntil;
    private bool _cameraRenderPending;
    private bool _bridgeReady;
    private IntPtr _renderEvent;
    private int _copyEventId;
    private int _dxgiFormat;
    private ulong _adapterLuid;
    private string _adapterName = string.Empty;
    private string _sharedName = string.Empty;
    private string _sourceCameraName = string.Empty;
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
                camera.orthographic = command.Flag;
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
        var camera = _camera;
        if (!_bridgeReady || camera is null)
        {
            return;
        }

        try
        {
            // Camera.Render() bypasses/does not reliably participate in SRP (URP/HDRP).
            // Pulse Camera.enabled for one normal Unity render-loop frame instead. On the
            // following Update the RenderTexture contains a fully completed frame, so the
            // native copy event can safely publish it to the Viewer.
            if (_cameraRenderPending)
            {
                camera.enabled = false;
                GL.IssuePluginEvent(_renderEvent, _copyEventId);
                _cameraRenderPending = false;
            }

            var now = Time.unscaledTime;
            if (now < _nextRenderAt)
            {
                return;
            }

            var interval = now < _interactiveUntil
                ? InteractiveRenderInterval
                : IdleRenderInterval;
            _nextRenderAt = now + interval;

            camera.enabled = true;
            _cameraRenderPending = true;
        }
        catch (Exception ex)
        {
            camera.enabled = false;
            _cameraRenderPending = false;
            _bridgeReady = false;
            _renderStatus = $"Scene render failed: {ex.Message}";
        }
    }

    public RenderTargetInfo GetRenderTargetInfo()
    {
        EnsureCamera();
        return new RenderTargetInfo
        {
            Available = _bridgeReady,
            SharedName = _sharedName,
            Width = RenderWidth,
            Height = RenderHeight,
            DxgiFormat = _dxgiFormat,
            AdapterLuid = _adapterLuid,
            AdapterName = _adapterName,
            Status = _renderStatus
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
        _camera.nearClipPlane = 0.03f;
        _camera.farClipPlane = 10000f;

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
                ? $"D3D11 shared Scene target ready on {DisplayAdapterName(_adapterName)}. Source Camera: {DisplayCameraName(_sourceCameraName)}. Unity render-loop capture."
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

    private static string DisplayCameraName(string value) =>
        string.IsNullOrWhiteSpace(value) ? "fallback camera" : value;

    private void CopyFromGameCamera()
    {
        var camera = _camera;
        if (camera is null)
        {
            return;
        }

        camera.orthographic = false;
        var source = FindSourceCamera(camera);
        if (source is null)
        {
            _sourceCameraName = string.Empty;
            camera.transform.position = new Vector3(0f, 2f, -5f);
            camera.transform.rotation = Quaternion.identity;
            camera.cullingMask = -1;
            return;
        }

        _sourceCameraName = source.name ?? source.gameObject.name ?? string.Empty;
        camera.transform.position = source.transform.position;
        camera.transform.rotation = source.transform.rotation;
        camera.fieldOfView = source.fieldOfView;
        camera.nearClipPlane = source.nearClipPlane;
        camera.farClipPlane = source.farClipPlane;
        camera.clearFlags = source.clearFlags;
        camera.backgroundColor = source.backgroundColor;
        camera.cullingMask = source.cullingMask;
        camera.depth = source.depth;
    }

    private static Camera? FindSourceCamera(Camera viewerCamera)
    {
        var main = Camera.main;
        if (main is not null && main != viewerCamera && main.enabled && main.gameObject.activeInHierarchy)
        {
            return main;
        }

        Camera? best = null;
        var bestDepth = float.PositiveInfinity;
        var cameras = Camera.allCameras;

        for (var index = 0; index < cameras.Length; index++)
        {
            var candidate = cameras[index];
            if (candidate is null || candidate == viewerCamera || !candidate.enabled || !candidate.gameObject.activeInHierarchy)
            {
                continue;
            }

            // Prefer a normal display camera over cameras already rendering into a texture.
            if (candidate.targetTexture is null)
            {
                if (best is null || best.targetTexture is not null || candidate.depth < bestDepth)
                {
                    best = candidate;
                    bestDepth = candidate.depth;
                }
            }
            else if (best is null)
            {
                best = candidate;
                bestDepth = candidate.depth;
            }
        }

        return best;
    }

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
            _camera.enabled = false;
            _camera.targetTexture = null;
        }

        _cameraRenderPending = false;

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
