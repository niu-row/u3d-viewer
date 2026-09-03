using System.Globalization;

namespace U3DViewer.Protocol;

public enum ViewerCommandKind
{
    CameraMove,
    CameraLook,
    CameraSpeed,
    CameraProjection,
    CameraLens,
    CameraReset,
    CameraFocus,
    SelectObject
}

public readonly struct ViewerCommand
{
    public ViewerCommand(ViewerCommandKind kind, float x = 0, float y = 0, float z = 0, float value = 0, int instanceId = 0, bool flag = false)
    {
        Kind = kind;
        X = x;
        Y = y;
        Z = z;
        Value = value;
        InstanceId = instanceId;
        Flag = flag;
    }

    public ViewerCommandKind Kind { get; }
    public float X { get; }
    public float Y { get; }
    public float Z { get; }
    public float Value { get; }
    public int InstanceId { get; }
    public bool Flag { get; }
}

public static class ViewerCommandCodec
{
    public static string EncodeCameraMove(float forward, float right, float up, float deltaSeconds) =>
        string.Join("\t", "camera.move", F(forward), F(right), F(up), F(deltaSeconds));

    public static string EncodeCameraLook(float yawDelta, float pitchDelta) =>
        string.Join("\t", "camera.look", F(yawDelta), F(pitchDelta));

    public static string EncodeCameraSpeed(float unitsPerSecond) =>
        string.Join("\t", "camera.speed", F(unitsPerSecond));

    public static string EncodeCameraProjection(bool orthographic) =>
        $"camera.projection\t{(orthographic ? "orthographic" : "perspective")}";

    public static string EncodeCameraLens(float fieldOfView, float nearClip, float farClip, float orthographicSize) =>
        string.Join("\t", "camera.lens", F(fieldOfView), F(nearClip), F(farClip), F(orthographicSize));

    public static string EncodeCameraReset() => "camera.reset";

    public static string EncodeCameraFocus(int instanceId) =>
        string.Join("\t", "camera.focus", instanceId.ToString(CultureInfo.InvariantCulture));

    public static string EncodeSelectObject(int instanceId) =>
        string.Join("\t", "selection.set", instanceId.ToString(CultureInfo.InvariantCulture));

    public static bool TryParse(string line, out ViewerCommand command)
    {
        command = default;
        if (string.IsNullOrWhiteSpace(line))
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

            case "camera.reset" when parts.Length == 1:
                command = new ViewerCommand(ViewerCommandKind.CameraReset);
                return true;

            case "camera.focus" when parts.Length == 2 &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var instanceId):
                command = new ViewerCommand(ViewerCommandKind.CameraFocus, instanceId: instanceId);
                return true;

            case "selection.set" when parts.Length == 2 &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var selectedInstanceId):
                command = new ViewerCommand(ViewerCommandKind.SelectObject, instanceId: selectedInstanceId);
                return true;

            default:
                return false;
        }
    }

    private static string F(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static bool TryF(string value, out float result) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
}
