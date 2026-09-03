namespace U3DViewer.Protocol;

public sealed class SceneSnapshot
{
    public string Type { get; set; } = "scene_snapshot";
    public long Sequence { get; set; }
    public long UnixTimeMs { get; set; }
    public RenderTargetInfo? RenderTarget { get; set; }
    public PerformanceInfo Performance { get; set; } = new();
    public SceneInfo[] Scenes { get; set; } = Array.Empty<SceneInfo>();
}

public sealed class RenderTargetInfo
{
    public bool Available { get; set; }
    public string SharedName { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public int DxgiFormat { get; set; }
    public ulong AdapterLuid { get; set; }
    public string AdapterName { get; set; } = string.Empty;
    public int NativeBridgeAbiVersion { get; set; }
    public bool Orthographic { get; set; }
    public float FieldOfView { get; set; }
    public float NearClipPlane { get; set; }
    public float FarClipPlane { get; set; }
    public float OrthographicSize { get; set; }
    public float MoveSpeed { get; set; }
    public float IdleFps { get; set; }
    public float InteractiveFps { get; set; }
    public SceneCullingMode CullingMode { get; set; } = SceneCullingMode.MainCamera;
    public int CullingMask { get; set; } = -1;
    public string[] LayerNames { get; set; } = Array.Empty<string>();
    public string Status { get; set; } = string.Empty;
}

public sealed class PerformanceInfo
{
    public double GameFps { get; set; }
    public double SceneFps { get; set; }
    public int HierarchyNodes { get; set; }
    public double HierarchyScanMs { get; set; }
    public double HierarchyScanAverageMs { get; set; }
    public double HierarchyScanMaxMs { get; set; }
    public double SceneRenderMs { get; set; }
    public double SceneRenderAverageMs { get; set; }
    public double SceneRenderMaxMs { get; set; }
    public double SnapshotSerializeMs { get; set; }
    public int SnapshotBytes { get; set; }
}

public sealed class SceneInfo
{
    public int BuildIndex { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsLoaded { get; set; }
    public GameObjectInfo[] Roots { get; set; } = Array.Empty<GameObjectInfo>();
}

public sealed class GameObjectInfo
{
    public int InstanceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool ActiveSelf { get; set; }
    public bool ActiveInHierarchy { get; set; }
    public int ChildCount { get; set; }
    public int Layer { get; set; }
    public string Tag { get; set; } = string.Empty;
    public TransformInfo Transform { get; set; } = new();
    public string[] Components { get; set; } = Array.Empty<string>();
    public GameObjectInfo[] Children { get; set; } = Array.Empty<GameObjectInfo>();
}

public sealed class TransformInfo
{
    public Vector3Info Position { get; set; } = new();
    public Vector3Info LocalPosition { get; set; } = new();
    public Vector3Info EulerAngles { get; set; } = new();
    public Vector3Info LocalScale { get; set; } = new();
}

public sealed class Vector3Info
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
}
