using System.Diagnostics;
using Il2CppInterop.Runtime;
using U3DViewer.Protocol;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace U3DViewer.Agent.IL2CPP;

internal static class SceneScanner
{
    public static SceneScanSession Begin(long sequence, int selectedInstanceId, HashSet<int> expandedInstanceIds)
    {
        var scenes = new List<SceneInfo>();
        var pending = new Queue<SceneScanWorkItem>();
        var sceneCount = SceneManager.sceneCount;

        for (var sceneIndex = 0; sceneIndex < sceneCount; sceneIndex++)
        {
            try
            {
                var scene = SceneManager.GetSceneAt(sceneIndex);
                var isLoaded = scene.isLoaded;
                var roots = Array.Empty<GameObject>();

                if (isLoaded)
                {
                    try
                    {
                        roots = scene.GetRootGameObjects();
                    }
                    catch (Exception)
                    {
                        // Additive scenes can remain visible through SceneManager for a short
                        // window while their native scene is already unloading (or not fully
                        // loaded yet). One transient scene must not abort the entire snapshot.
                        isLoaded = false;
                        roots = Array.Empty<GameObject>();
                    }
                }

                var rootInfos = new GameObjectInfo[roots.Length];
                scenes.Add(new SceneInfo
                {
                    BuildIndex = scene.buildIndex,
                    Name = scene.name ?? string.Empty,
                    IsLoaded = isLoaded,
                    Roots = rootInfos
                });

                for (var rootIndex = 0; rootIndex < roots.Length; rootIndex++)
                {
                    pending.Enqueue(new SceneScanWorkItem(roots[rootIndex], rootInfos, rootIndex));
                }
            }
            catch (Exception)
            {
                // SceneManager can change between reading sceneCount and GetSceneAt while
                // additive content is loading/unloading. Retry from a fresh list next tick.
            }
        }

        return new SceneScanSession(
            new SceneSnapshot
            {
                Sequence = sequence,
                UnixTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Scenes = scenes.ToArray()
            },
            selectedInstanceId,
            expandedInstanceIds,
            pending);
    }

    internal sealed class SceneScanSession
    {
        private readonly int _selectedInstanceId;
        private readonly HashSet<int> _expandedInstanceIds;
        private readonly Queue<SceneScanWorkItem> _pending;

        internal SceneScanSession(
            SceneSnapshot snapshot,
            int selectedInstanceId,
            HashSet<int> expandedInstanceIds,
            Queue<SceneScanWorkItem> pending)
        {
            Snapshot = snapshot;
            _selectedInstanceId = selectedInstanceId;
            _expandedInstanceIds = expandedInstanceIds;
            _pending = pending;
        }

        public SceneSnapshot Snapshot { get; }
        public bool IsComplete => _pending.Count == 0;

        public int ProcessSlice(int maxNodes, double budgetMilliseconds)
        {
            if (maxNodes <= 0 || _pending.Count == 0)
            {
                return 0;
            }

            var processed = 0;
            var start = Stopwatch.GetTimestamp();
            var budgetTicks = budgetMilliseconds <= 0
                ? long.MaxValue
                : Math.Max(1L, (long)(Stopwatch.Frequency * budgetMilliseconds / 1000.0));

            while (_pending.Count > 0 && processed < maxNodes)
            {
                ProcessOne(_pending.Dequeue());
                processed++;

                if (Stopwatch.GetTimestamp() - start >= budgetTicks)
                {
                    break;
                }
            }

            return processed;
        }

        private void ProcessOne(SceneScanWorkItem item)
        {
            try
            {
                var gameObject = item.GameObject;
                if (gameObject is null)
                {
                    item.Target[item.Index] = UnavailableObject();
                    return;
                }

                var instanceId = gameObject.GetInstanceID();
                var transform = gameObject.transform;
                var childCount = transform.childCount;
                var shouldLoadChildren = childCount > 0 && _expandedInstanceIds.Contains(instanceId);
                var children = shouldLoadChildren
                    ? new GameObjectInfo[childCount]
                    : Array.Empty<GameObjectInfo>();

                var transformInfo = new TransformInfo();
                var componentNames = Array.Empty<string>();
                var layer = 0;
                var tag = string.Empty;

                if (instanceId == _selectedInstanceId)
                {
                    layer = gameObject.layer;
                    tag = ReadTag(gameObject);
                    transformInfo = new TransformInfo
                    {
                        Position = ToInfo(transform.position),
                        LocalPosition = ToInfo(transform.localPosition),
                        EulerAngles = ToInfo(transform.eulerAngles),
                        LocalScale = ToInfo(transform.localScale)
                    };

                    var components = gameObject.GetComponents(Il2CppType.Of<Component>());
                    componentNames = new string[components.Length];
                    for (var componentIndex = 0; componentIndex < components.Length; componentIndex++)
                    {
                        var component = components[componentIndex];
                        componentNames[componentIndex] = component is null
                            ? "<missing>"
                            : component.GetType().FullName ?? component.GetType().Name;
                    }
                }

                item.Target[item.Index] = new GameObjectInfo
                {
                    InstanceId = instanceId,
                    Name = gameObject.name ?? string.Empty,
                    ActiveSelf = gameObject.activeSelf,
                    ActiveInHierarchy = gameObject.activeInHierarchy,
                    ChildCount = childCount,
                    Layer = layer,
                    Tag = tag,
                    Transform = transformInfo,
                    Components = componentNames,
                    Children = children
                };

                if (!shouldLoadChildren)
                {
                    return;
                }

                for (var childIndex = 0; childIndex < childCount; childIndex++)
                {
                    try
                    {
                        var child = transform.GetChild(childIndex);
                        if (child is null)
                        {
                            children[childIndex] = UnavailableObject();
                            continue;
                        }

                        _pending.Enqueue(new SceneScanWorkItem(child.gameObject, children, childIndex));
                    }
                    catch (Exception)
                    {
                        children[childIndex] = UnavailableObject();
                    }
                }
            }
            catch (Exception)
            {
                item.Target[item.Index] = UnavailableObject();
            }
        }
    }

    internal readonly struct SceneScanWorkItem
    {
        public SceneScanWorkItem(GameObject gameObject, GameObjectInfo[] target, int index)
        {
            GameObject = gameObject;
            Target = target;
            Index = index;
        }

        public GameObject GameObject { get; }
        public GameObjectInfo[] Target { get; }
        public int Index { get; }
    }

    private static GameObjectInfo UnavailableObject() => new()
    {
        Name = "<unavailable>",
        ActiveSelf = false,
        ActiveInHierarchy = false,
        ChildCount = 0
    };

    private static string ReadTag(GameObject gameObject)
    {
        try
        {
            return gameObject.tag ?? string.Empty;
        }
        catch (Exception)
        {
            // IL2CPP UnityException is a generated proxy type rather than a CLR exception type.
            // Il2CppInterop surfaces invocation failures through CLR exceptions, so catch those here.
            return string.Empty;
        }
    }

    private static Vector3Info ToInfo(Vector3 value) => new()
    {
        X = value.x,
        Y = value.y,
        Z = value.z
    };
}
