using U3DViewer.Protocol;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace U3DViewer.Agent.IL2CPP;

internal sealed class SceneCameraController : IDisposable
{
    private GameObject? _cameraObject;
    private Camera? _camera;
    private float _moveSpeed = 10f;

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
                break;
            }
            case ViewerCommandKind.CameraLook:
            {
                var euler = transform.eulerAngles;
                var pitch = NormalizePitch(euler.x + command.Y);
                var yaw = euler.y + command.X;
                transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
                break;
            }
            case ViewerCommandKind.CameraSpeed:
                _moveSpeed = Mathf.Clamp(command.Value, 0.1f, 1000f);
                break;
            case ViewerCommandKind.CameraProjection:
                camera.orthographic = command.Flag;
                break;
            case ViewerCommandKind.CameraReset:
                CopyFromGameCamera();
                break;
            case ViewerCommandKind.CameraFocus:
            {
                var target = FindGameObject(command.InstanceId);
                if (target is not null)
                {
                    Focus(target.transform.position);
                }
                break;
            }
        }
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
        _camera.nearClipPlane = 0.03f;
        _camera.farClipPlane = 10000f;
        CopyFromGameCamera();
    }

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
            camera.transform.position = new Vector3(0f, 2f, -5f);
            camera.transform.rotation = Quaternion.identity;
            return;
        }

        camera.transform.position = source.transform.position;
        camera.transform.rotation = source.transform.rotation;
        camera.orthographic = source.orthographic;
        camera.orthographicSize = source.orthographicSize;
        camera.fieldOfView = source.fieldOfView;
        camera.nearClipPlane = source.nearClipPlane;
        camera.farClipPlane = source.farClipPlane;
        camera.clearFlags = source.clearFlags;
        camera.backgroundColor = source.backgroundColor;
        camera.cullingMask = source.cullingMask;
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
        if (_cameraObject is not null)
        {
            UnityEngine.Object.Destroy(_cameraObject);
            _cameraObject = null;
            _camera = null;
        }
    }
}
