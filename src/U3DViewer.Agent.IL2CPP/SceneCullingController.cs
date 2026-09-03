using U3DViewer.Protocol;
using UnityEngine;

namespace U3DViewer.Agent.IL2CPP;

internal sealed class SceneCullingController
{
    private const string ViewerCameraObjectName = "__U3DViewerCamera";

    private readonly string[] _layerNames = CaptureLayerNames();
    private SceneCullingMode _mode = SceneCullingMode.MainCamera;
    private int _manualMask = -1;
    private Camera? _viewerCamera;

    public void Apply(ViewerCommand command)
    {
        if (command.Kind != ViewerCommandKind.CameraCullingMask)
        {
            return;
        }

        _mode = command.CullingMode;
        if (_mode == SceneCullingMode.Manual)
        {
            _manualMask = command.CullingMask;
        }

        ApplyCurrentMode();
    }

    public void Reapply() => ApplyCurrentMode();

    public void Populate(RenderTargetInfo? target)
    {
        if (target is null)
        {
            return;
        }

        var camera = FindViewerCamera();
        if (camera is not null)
        {
            camera.cullingMask = _mode == SceneCullingMode.MainCamera
                ? ResolveSourceCameraMask(target.SourceCameraInstanceId)
                : ResolveMask();
        }

        target.CullingMode = _mode;
        target.CullingMask = camera is null
            ? (_mode == SceneCullingMode.MainCamera
                ? ResolveSourceCameraMask(target.SourceCameraInstanceId)
                : ResolveMask())
            : camera.cullingMask;
        target.LayerNames = _layerNames;
    }

    private void ApplyCurrentMode()
    {
        var camera = FindViewerCamera();
        if (camera is not null)
        {
            camera.cullingMask = ResolveMask();
        }
    }

    private int ResolveMask()
    {
        return _mode switch
        {
            SceneCullingMode.All => -1,
            SceneCullingMode.Manual => _manualMask,
            _ => ResolveMainCameraMask()
        };
    }

    private int ResolveSourceCameraMask(int sourceCameraInstanceId)
    {
        if (sourceCameraInstanceId != 0)
        {
            var cameras = Camera.allCameras;
            for (var index = 0; index < cameras.Length; index++)
            {
                var source = cameras[index];
                if (source is not null &&
                    source != FindViewerCamera() &&
                    source.GetInstanceID() == sourceCameraInstanceId)
                {
                    return source.cullingMask;
                }
            }
        }

        return ResolveMainCameraMask();
    }

    private int ResolveMainCameraMask()
    {
        var main = Camera.main;
        var viewer = FindViewerCamera();
        return main is null || main == viewer ? -1 : main.cullingMask;
    }

    private Camera? FindViewerCamera()
    {
        if (_viewerCamera is not null)
        {
            return _viewerCamera;
        }

        var cameraObject = GameObject.Find(ViewerCameraObjectName);
        if (cameraObject is null)
        {
            return null;
        }

        _viewerCamera = cameraObject.GetComponent<Camera>();
        return _viewerCamera;
    }

    private static string[] CaptureLayerNames()
    {
        var names = new string[32];
        for (var layer = 0; layer < names.Length; layer++)
        {
            names[layer] = LayerMask.LayerToName(layer) ?? string.Empty;
        }
        return names;
    }
}
