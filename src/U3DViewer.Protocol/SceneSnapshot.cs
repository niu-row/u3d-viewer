namespace U3DViewer.Protocol;

public sealed class SceneSnapshot
{
    public string Type { get; set; } = "scene_snapshot";
    public long Sequence { get; set; }
    public long UnixTimeMs { get; set; }
    public RenderTargetInfo? RenderTarget { get; set; }
    public SceneInfo[] Scenes { get; set; } = Array.Empty<SceneInfo>();
}

public sealed class RenderTargetInfo
{
    public bool Available { get; set; }
    public string SharedName { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public int DxgiFormat { get; set; }
    public string Status { get; set; } = string.Empty;
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
