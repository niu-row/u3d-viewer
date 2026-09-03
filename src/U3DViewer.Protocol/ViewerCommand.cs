using System;
using System.Globalization;

namespace U3DViewer.Protocol;

public enum SceneCullingMode
{
    All = 0,
    MainCamera = 1,
    Manual = 2
}

public enum ViewerCommandKind
{
    CameraMove,
    CameraLook,
    CameraSpeed,
    CameraProjection,
    CameraLens,
    CameraStreamSettings,
    CameraCullingMask,
    CameraFollowTransform,
    CameraSource,
    CameraReset,
    CameraRecover,
    CameraVisibility,
    CameraFocus,
    SelectObject,
    HierarchyExpanded,
    SceneExpanded
}

public struct ViewerCommand
{
    public ViewerCommand(
        ViewerCommandKind kind,
        float x = 0,
        float y = 0,
        float z = 0,
        float value = 0,
        int instanceId = 0,
        bool flag = false,
        bool flag2 = false,
        SceneCullingMode cullingMode = SceneCullingMode.MainCamera,
        int cullingMask = -1)
    {
        Kind = kind;
        X = x;
        Y = y;
        Z = z;
        Value = value;
        InstanceId = instanceId;
        Flag = flag;
        Flag2 = flag2;
        CullingMode = cullingMode;
        CullingMask = cullingMask;
    }

    public ViewerCommandKind Kind { get; private set; }
    public float X { get; private set; }
    public float Y { get; private set; }
    public float Z { get; private set; }
    public float Value { get; private set; }
    public int InstanceId { get; private set; }
    public bool Flag { get; private set; }
    public bool Flag2 { get; private set; }
    public SceneCullingMode CullingMode { get; private set; }
    public int CullingMask { get; private set; }
}

public static class ViewerCommandCodec
{
    public static string EncodeCameraMove(float forward, float right, float up, float deltaSeconds) =>
        Join("camera.move", F(forward), F(right), F(up), F(deltaSeconds));

    public static string EncodeCameraLook(float yawDelta, float pitchDelta) =>
        Join("camera.look", F(yawDelta), F(pitchDelta));

    public static string EncodeCameraSpeed(float unitsPerSecond) =>
        Join("camera.speed", F(unitsPerSecond));

    public static string EncodeCameraProjection(bool orthographic) =>
        $"camera.projection\t{(orthographic ? "orthographic" : "perspective")}";

    public static string EncodeCameraLens(float fieldOfView, float nearClip, float farClip, float orthographicSize) =>
        Join("camera.lens", F(fieldOfView), F(nearClip), F(farClip), F(orthographicSize));

    public static string EncodeCameraStreamSettings(float idleFps, float interactiveFps, int width, int height) =>
        Join(
            "camera.stream",
            F(idleFps),
            F(interactiveFps),
            width.ToString(CultureInfo.InvariantCulture),
            height.ToString(CultureInfo.InvariantCulture));

    public static string EncodeCameraCulling(SceneCullingMode mode, int mask) =>
        Join(
            "camera.culling",
            ((int)mode).ToString(CultureInfo.InvariantCulture),
            mask.ToString(CultureInfo.InvariantCulture));

    public static string EncodeCameraFollowTransform(bool position, bool rotation) =>
        Join("camera.follow", position ? "1" : "0", rotation ? "1" : "0");

    public static string EncodeCameraSource(int instanceId) =>
        Join("camera.source", instanceId.ToString(CultureInfo.InvariantCulture));

    public static string EncodeCameraReset() => "camera.reset";

    public static string EncodeCameraRecover() => "camera.recover";

    public static string EncodeCameraVisibility(bool visible) =>
        $"camera.visibility\t{(visible ? "1" : "0")}";

    public static string EncodeCameraFocus(int instanceId) =>
        Join("camera.focus", instanceId.ToString(CultureInfo.InvariantCulture));

    public static string EncodeSelectObject(int instanceId) =>
        Join("selection.set", instanceId.ToString(CultureInfo.InvariantCulture));

    public static string EncodeHierarchyExpanded(int instanceId, bool expanded) =>
        Join(
            "hierarchy.expanded",
            instanceId.ToString(CultureInfo.InvariantCulture),
            expanded ? "1" : "0");

    public static string EncodeSceneExpanded(int buildIndex, string sceneName, bool expanded) =>
        Join(
            "hierarchy.scene",
            buildIndex.ToString(CultureInfo.InvariantCulture),
            StableSceneNameHash(sceneName).ToString(CultureInfo.InvariantCulture),
            expanded ? "1" : "0");

    public static long BuildSceneKey(int buildIndex, string sceneName) =>
        BuildSceneKey(buildIndex, StableSceneNameHash(sceneName));

    public static long BuildSceneKey(int buildIndex, int sceneNameHash) =>
        ((long)(uint)buildIndex << 32) | (uint)sceneNameHash;

    public static bool TryParse(string line, out ViewerCommand command)
    {
        command = default(ViewerCommand);
        if (IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var parts = line.Split('\t');
        switch (parts[0])
        {
            case "camera.move" when parts.Length == 5 &&
                TryF(parts[1], out var forward) &&
                TryF(parts[2], out var right) &&
                TryF(parts[3], out var up) &&
                TryF(parts[4], out var deltaSeconds):
                command = new ViewerCommand(ViewerCommandKind.CameraMove, forward, right, up, deltaSeconds);
                return true;

            case "camera.look" when parts.Length == 3 &&
                TryF(parts[1], out var yaw) &&
                TryF(parts[2], out var pitch):
                command = new ViewerCommand(ViewerCommandKind.CameraLook, yaw, pitch);
                return true;

            case "camera.speed" when parts.Length == 2 && TryF(parts[1], out var speed):
                command = new ViewerCommand(ViewerCommandKind.CameraSpeed, value: speed);
                return true;

            case "camera.projection" when parts.Length == 2:
                if (parts[1] == "orthographic")
                {
                    command = new ViewerCommand(ViewerCommandKind.CameraProjection, flag: true);
                    return true;
                }
                if (parts[1] == "perspective")
                {
                    command = new ViewerCommand(ViewerCommandKind.CameraProjection, flag: false);
                    return true;
                }
                return false;

            case "camera.lens" when parts.Length == 5 &&
                TryF(parts[1], out var fieldOfView) &&
                TryF(parts[2], out var nearClip) &&
                TryF(parts[3], out var farClip) &&
                TryF(parts[4], out var orthographicSize):
                command = new ViewerCommand(
                    ViewerCommandKind.CameraLens,
                    fieldOfView,
                    nearClip,
                    farClip,
                    orthographicSize);
                return true;

            case "camera.stream" when parts.Length == 5 &&
                TryF(parts[1], out var idleFps) &&
                TryF(parts[2], out var interactiveFps) &&
                int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) &&
                int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height):
                command = new ViewerCommand(
                    ViewerCommandKind.CameraStreamSettings,
                    idleFps,
                    interactiveFps,
                    width,
                    height);
                return true;

            case "camera.culling" when parts.Length == 3 &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var cullingModeValue) &&
                int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var cullingMask) &&
                cullingModeValue >= (int)SceneCullingMode.All &&
                cullingModeValue <= (int)SceneCullingMode.Manual:
                command = new ViewerCommand(
                    ViewerCommandKind.CameraCullingMask,
                    cullingMode: (SceneCullingMode)cullingModeValue,
                    cullingMask: cullingMask);
                return true;

            case "camera.follow" when parts.Length == 3 &&
                (parts[1] == "0" || parts[1] == "1") &&
                (parts[2] == "0" || parts[2] == "1"):
                command = new ViewerCommand(
                    ViewerCommandKind.CameraFollowTransform,
                    flag: parts[1] == "1",
                    flag2: parts[2] == "1");
                return true;

            case "camera.source" when parts.Length == 2 &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sourceCameraInstanceId):
                command = new ViewerCommand(
                    ViewerCommandKind.CameraSource,
                    instanceId: sourceCameraInstanceId);
                return true;

            case "camera.reset" when parts.Length == 1:
                command = new ViewerCommand(ViewerCommandKind.CameraReset);
                return true;

            case "camera.recover" when parts.Length == 1:
                command = new ViewerCommand(ViewerCommandKind.CameraRecover);
                return true;

            case "camera.visibility" when parts.Length == 2 &&
                (parts[1] == "0" || parts[1] == "1"):
                command = new ViewerCommand(
                    ViewerCommandKind.CameraVisibility,
                    flag: parts[1] == "1");
                return true;

            case "camera.focus" when parts.Length == 2 &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var instanceId):
                command = new ViewerCommand(ViewerCommandKind.CameraFocus, instanceId: instanceId);
                return true;

            case "selection.set" when parts.Length == 2 &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var selectedInstanceId):
                command = new ViewerCommand(ViewerCommandKind.SelectObject, instanceId: selectedInstanceId);
                return true;

            case "hierarchy.expanded" when parts.Length == 3 &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var expandedInstanceId) &&
                (parts[2] == "0" || parts[2] == "1"):
                command = new ViewerCommand(
                    ViewerCommandKind.HierarchyExpanded,
                    instanceId: expandedInstanceId,
                    flag: parts[2] == "1");
                return true;

            case "hierarchy.scene" when parts.Length == 4 &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sceneBuildIndex) &&
                int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sceneNameHash) &&
                (parts[3] == "0" || parts[3] == "1"):
                command = new ViewerCommand(
                    ViewerCommandKind.SceneExpanded,
                    instanceId: sceneBuildIndex,
                    flag: parts[3] == "1",
                    cullingMask: sceneNameHash);
                return true;

            default:
                return false;
        }
    }

    private static int StableSceneNameHash(string value)
    {
        unchecked
        {
            uint hash = 2166136261u;
            value = value ?? string.Empty;
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                hash ^= (byte)(character & 0xff);
                hash *= 16777619u;
                hash ^= (byte)(character >> 8);
                hash *= 16777619u;
            }
            return (int)hash;
        }
    }

    private static bool IsNullOrWhiteSpace(string value)
    {
        if (value == null)
        {
            return true;
        }

        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsWhiteSpace(value[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static string Join(params string[] parts) => string.Join("\t", parts);

    private static string F(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static bool TryF(string value, out float result) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
}
