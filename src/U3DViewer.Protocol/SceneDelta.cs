namespace U3DViewer.Protocol;

public sealed class SceneDelta
{
    public string Type { get; set; } = "scene_delta";
    public long Sequence { get; set; }
    public long UnixTimeMs { get; set; }
    public RenderTargetInfo? RenderTarget { get; set; }
    public SceneInfo[] Scenes { get; set; } = Array.Empty<SceneInfo>();
    public int[] RemovedInstanceIds { get; set; } = Array.Empty<int>();
    public SceneNodeDelta[] Upserts { get; set; } = Array.Empty<SceneNodeDelta>();
}

public sealed class SceneNodeDelta
{
    public int InstanceId { get; set; }
    public int SceneBuildIndex { get; set; }
    public string SceneName { get; set; } = string.Empty;
    public int ParentInstanceId { get; set; }
    public int SiblingIndex { get; set; }
    public GameObjectInfo GameObject { get; set; } = new();
}

public static class SceneDeltaBuilder
{
    public static SceneDelta Build(SceneSnapshot previous, SceneSnapshot current)
    {
        var previousNodes = Flatten(previous);
        var currentNodes = Flatten(current);
        var removed = previousNodes.Keys
            .Where(instanceId => !currentNodes.ContainsKey(instanceId))
            .ToArray();
        var upserts = new List<SceneNodeDelta>();

        foreach (var entry in currentNodes.OrderBy(item => item.Value.Order))
        {
            if (!previousNodes.TryGetValue(entry.Key, out var oldNode) || !Equivalent(oldNode, entry.Value))
            {
                var node = entry.Value;
                upserts.Add(new SceneNodeDelta
                {
                    InstanceId = entry.Key,
                    SceneBuildIndex = node.SceneBuildIndex,
                    SceneName = node.SceneName,
                    ParentInstanceId = node.ParentInstanceId,
                    SiblingIndex = node.SiblingIndex,
                    GameObject = WithoutChildren(node.Info)
                });
            }
        }

        return new SceneDelta
        {
            Sequence = current.Sequence,
            UnixTimeMs = current.UnixTimeMs,
            RenderTarget = current.RenderTarget,
            Scenes = current.Scenes.Select(scene => new SceneInfo
            {
                BuildIndex = scene.BuildIndex,
                Name = scene.Name,
                IsLoaded = scene.IsLoaded,
                Roots = Array.Empty<GameObjectInfo>()
            }).ToArray(),
            RemovedInstanceIds = removed,
            Upserts = upserts.ToArray()
        };
    }

    private static Dictionary<int, FlatNode> Flatten(SceneSnapshot snapshot)
    {
        var result = new Dictionary<int, FlatNode>();
        var order = 0;

        foreach (var scene in snapshot.Scenes)
        {
            for (var siblingIndex = 0; siblingIndex < scene.Roots.Length; siblingIndex++)
            {
                FlattenNode(
                    scene.Roots[siblingIndex],
                    scene.BuildIndex,
                    scene.Name,
                    parentInstanceId: 0,
                    siblingIndex,
                    ref order,
                    result);
            }
        }

        return result;
    }

    private static void FlattenNode(
        GameObjectInfo info,
        int sceneBuildIndex,
        string sceneName,
        int parentInstanceId,
        int siblingIndex,
        ref int order,
        Dictionary<int, FlatNode> result)
    {
        if (info.InstanceId == 0)
        {
            return;
        }

        result[info.InstanceId] = new FlatNode(
            info,
            sceneBuildIndex,
            sceneName,
            parentInstanceId,
            siblingIndex,
            order++);

        for (var childIndex = 0; childIndex < info.Children.Length; childIndex++)
        {
            FlattenNode(
                info.Children[childIndex],
                sceneBuildIndex,
                sceneName,
                info.InstanceId,
                childIndex,
                ref order,
                result);
        }
    }

    private static bool Equivalent(FlatNode left, FlatNode right)
    {
        if (left.SceneBuildIndex != right.SceneBuildIndex ||
            !string.Equals(left.SceneName, right.SceneName, StringComparison.Ordinal) ||
            left.ParentInstanceId != right.ParentInstanceId ||
            left.SiblingIndex != right.SiblingIndex)
        {
            return false;
        }

        var a = left.Info;
        var b = right.Info;
        return a.InstanceId == b.InstanceId &&
               string.Equals(a.Name, b.Name, StringComparison.Ordinal) &&
               a.ActiveSelf == b.ActiveSelf &&
               a.ActiveInHierarchy == b.ActiveInHierarchy &&
               a.Layer == b.Layer &&
               string.Equals(a.Tag, b.Tag, StringComparison.Ordinal) &&
               Equivalent(a.Transform, b.Transform) &&
               a.Components.SequenceEqual(b.Components, StringComparer.Ordinal);
    }

    private static bool Equivalent(TransformInfo left, TransformInfo right) =>
        Equivalent(left.Position, right.Position) &&
        Equivalent(left.LocalPosition, right.LocalPosition) &&
        Equivalent(left.EulerAngles, right.EulerAngles) &&
        Equivalent(left.LocalScale, right.LocalScale);

    private static bool Equivalent(Vector3Info left, Vector3Info right) =>
        left.X == right.X && left.Y == right.Y && left.Z == right.Z;

    private static GameObjectInfo WithoutChildren(GameObjectInfo value) => new()
    {
        InstanceId = value.InstanceId,
        Name = value.Name,
        ActiveSelf = value.ActiveSelf,
        ActiveInHierarchy = value.ActiveInHierarchy,
        Layer = value.Layer,
        Tag = value.Tag,
        Transform = value.Transform,
        Components = value.Components,
        Children = Array.Empty<GameObjectInfo>()
    };

    private sealed class FlatNode
    {
        public FlatNode(
            GameObjectInfo info,
            int sceneBuildIndex,
            string sceneName,
            int parentInstanceId,
            int siblingIndex,
            int order)
        {
            Info = info;
            SceneBuildIndex = sceneBuildIndex;
            SceneName = sceneName;
            ParentInstanceId = parentInstanceId;
            SiblingIndex = siblingIndex;
            Order = order;
        }

        public GameObjectInfo Info { get; }
        public int SceneBuildIndex { get; }
        public string SceneName { get; }
        public int ParentInstanceId { get; }
        public int SiblingIndex { get; }
        public int Order { get; }
    }
}
