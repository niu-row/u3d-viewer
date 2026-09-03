using System.Globalization;
using System.Text;
using U3DViewer.Protocol;

namespace U3DViewer.Agent.Mono;

internal static class JsonSnapshotWriter
{
    public static string Write(SceneSnapshot snapshot)
    {
        var sb = new StringBuilder(16 * 1024);
        sb.Append('{');
        Property(sb, "type", snapshot.Type); sb.Append(',');
        NumberProperty(sb, "sequence", snapshot.Sequence); sb.Append(',');
        NumberProperty(sb, "unixTimeMs", snapshot.UnixTimeMs); sb.Append(',');
        sb.Append("\"renderTarget\":");
        WriteRenderTarget(sb, snapshot.RenderTarget);
        sb.Append(',');
        sb.Append("\"performance\":");
        WritePerformance(sb, snapshot.Performance);
        sb.Append(',');
        sb.Append("\"scenes\":[");
        for (var i = 0; i < snapshot.Scenes.Length; i++)
        {
            if (i > 0) sb.Append(',');
            WriteScene(sb, snapshot.Scenes[i]);
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private static void WriteRenderTarget(StringBuilder sb, RenderTargetInfo? target)
    {
        if (target is null)
        {
            sb.Append("null");
            return;
        }

        sb.Append('{');
        BoolProperty(sb, "available", target.Available); sb.Append(',');
        Property(sb, "sharedName", target.SharedName); sb.Append(',');
        NumberProperty(sb, "width", target.Width); sb.Append(',');
        NumberProperty(sb, "height", target.Height); sb.Append(',');
        NumberProperty(sb, "dxgiFormat", target.DxgiFormat); sb.Append(',');
        UnsignedNumberProperty(sb, "adapterLuid", target.AdapterLuid); sb.Append(',');
        Property(sb, "adapterName", target.AdapterName); sb.Append(',');
        BoolProperty(sb, "orthographic", target.Orthographic); sb.Append(',');
        FloatProperty(sb, "fieldOfView", target.FieldOfView); sb.Append(',');
        FloatProperty(sb, "nearClipPlane", target.NearClipPlane); sb.Append(',');
        FloatProperty(sb, "farClipPlane", target.FarClipPlane); sb.Append(',');
        FloatProperty(sb, "orthographicSize", target.OrthographicSize); sb.Append(',');
        FloatProperty(sb, "moveSpeed", target.MoveSpeed); sb.Append(',');
        FloatProperty(sb, "idleFps", target.IdleFps); sb.Append(',');
        FloatProperty(sb, "interactiveFps", target.InteractiveFps); sb.Append(',');
        NumberProperty(sb, "cullingMode", (int)target.CullingMode); sb.Append(',');
        NumberProperty(sb, "cullingMask", target.CullingMask); sb.Append(',');
        sb.Append("\"layerNames\":[");
        for (var i = 0; i < target.LayerNames.Length; i++)
        {
            if (i > 0) sb.Append(',');
            String(sb, target.LayerNames[i]);
        }
        sb.Append("],");
        Property(sb, "status", target.Status);
        sb.Append('}');
    }

    private static void WritePerformance(StringBuilder sb, PerformanceInfo performance)
    {
        sb.Append('{');
        DoubleProperty(sb, "gameFps", performance.GameFps); sb.Append(',');
        DoubleProperty(sb, "sceneFps", performance.SceneFps); sb.Append(',');
        NumberProperty(sb, "hierarchyNodes", performance.HierarchyNodes); sb.Append(',');
        DoubleProperty(sb, "hierarchyScanMs", performance.HierarchyScanMs); sb.Append(',');
        DoubleProperty(sb, "hierarchyScanAverageMs", performance.HierarchyScanAverageMs); sb.Append(',');
        DoubleProperty(sb, "hierarchyScanMaxMs", performance.HierarchyScanMaxMs); sb.Append(',');
        DoubleProperty(sb, "sceneRenderMs", performance.SceneRenderMs); sb.Append(',');
        DoubleProperty(sb, "sceneRenderAverageMs", performance.SceneRenderAverageMs); sb.Append(',');
        DoubleProperty(sb, "sceneRenderMaxMs", performance.SceneRenderMaxMs); sb.Append(',');
        DoubleProperty(sb, "snapshotSerializeMs", performance.SnapshotSerializeMs); sb.Append(',');
        NumberProperty(sb, "snapshotBytes", performance.SnapshotBytes);
        sb.Append('}');
    }

    private static void WriteScene(StringBuilder sb, SceneInfo scene)
    {
        sb.Append('{');
        NumberProperty(sb, "buildIndex", scene.BuildIndex); sb.Append(',');
        Property(sb, "name", scene.Name); sb.Append(',');
        BoolProperty(sb, "isLoaded", scene.IsLoaded); sb.Append(',');
        sb.Append("\"roots\":[");
        for (var i = 0; i < scene.Roots.Length; i++)
        {
            if (i > 0) sb.Append(',');
            WriteGameObject(sb, scene.Roots[i]);
        }
        sb.Append("]}");
    }

    private static void WriteGameObject(StringBuilder sb, GameObjectInfo go)
    {
        sb.Append('{');
        NumberProperty(sb, "instanceId", go.InstanceId); sb.Append(',');
        Property(sb, "name", go.Name); sb.Append(',');
        BoolProperty(sb, "activeSelf", go.ActiveSelf); sb.Append(',');
        BoolProperty(sb, "activeInHierarchy", go.ActiveInHierarchy); sb.Append(',');
        NumberProperty(sb, "childCount", go.ChildCount); sb.Append(',');
        NumberProperty(sb, "layer", go.Layer); sb.Append(',');
        Property(sb, "tag", go.Tag); sb.Append(',');
        sb.Append("\"transform\":"); WriteTransform(sb, go.Transform); sb.Append(',');
        sb.Append("\"components\":[");
        for (var i = 0; i < go.Components.Length; i++)
        {
            if (i > 0) sb.Append(',');
            String(sb, go.Components[i]);
        }
        sb.Append("],\"children\":[");
        for (var i = 0; i < go.Children.Length; i++)
        {
            if (i > 0) sb.Append(',');
            WriteGameObject(sb, go.Children[i]);
        }
        sb.Append("]}");
    }

    private static void WriteTransform(StringBuilder sb, TransformInfo transform)
    {
        sb.Append('{');
        sb.Append("\"position\":"); WriteVector(sb, transform.Position); sb.Append(',');
        sb.Append("\"localPosition\":"); WriteVector(sb, transform.LocalPosition); sb.Append(',');
        sb.Append("\"eulerAngles\":"); WriteVector(sb, transform.EulerAngles); sb.Append(',');
        sb.Append("\"localScale\":"); WriteVector(sb, transform.LocalScale);
        sb.Append('}');
    }

    private static void WriteVector(StringBuilder sb, Vector3Info value)
    {
        sb.Append('{');
        FloatProperty(sb, "x", value.X); sb.Append(',');
        FloatProperty(sb, "y", value.Y); sb.Append(',');
        FloatProperty(sb, "z", value.Z);
        sb.Append('}');
    }

    private static void Property(StringBuilder sb, string name, string value)
    {
        String(sb, name); sb.Append(':'); String(sb, value);
    }

    private static void NumberProperty(StringBuilder sb, string name, long value)
    {
        String(sb, name); sb.Append(':').Append(value.ToString(CultureInfo.InvariantCulture));
    }

    private static void UnsignedNumberProperty(StringBuilder sb, string name, ulong value)
    {
        String(sb, name); sb.Append(':').Append(value.ToString(CultureInfo.InvariantCulture));
    }

    private static void BoolProperty(StringBuilder sb, string name, bool value)
    {
        String(sb, name); sb.Append(':').Append(value ? "true" : "false");
    }

    private static void FloatProperty(StringBuilder sb, string name, float value)
    {
        String(sb, name); sb.Append(':').Append(value.ToString("R", CultureInfo.InvariantCulture));
    }

    private static void DoubleProperty(StringBuilder sb, string name, double value)
    {
        String(sb, name); sb.Append(':').Append(value.ToString("R", CultureInfo.InvariantCulture));
    }

    private static void String(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                    else sb.Append(ch);
                    break;
            }
        }
        sb.Append('"');
    }
}
