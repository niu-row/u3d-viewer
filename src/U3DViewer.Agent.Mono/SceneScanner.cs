using System.Diagnostics;
using U3DViewer.Protocol;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace U3DViewer.Agent.Mono;

internal static class SceneScanner
{
    public static SceneScanSession Begin(long sequence, int selectedInstanceId)
    {
        var scenes = new SceneInfo[SceneManager.sceneCount];
        var pending = new Queue<SceneScanWorkItem>();

        for (var sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
        {
            var scene = SceneManager.GetSceneAt(sceneIndex);
            var roots = scene.GetRootGameObjects();
            var rootInfos = new GameObjectInfo[roots.Length];

            scenes[sceneIndex] = new SceneInfo
            {
                BuildIndex = scene.buildIndex,
                Name = scene.name ?? string.Empty,
                IsLoaded = scene.isLoaded,
                Roots = rootInfos
            };

            for (var rootIndex = 0; rootIndex < roots.Length; rootIndex++)
            {
                pending.Enqueue(new SceneScanWorkItem(roots[rootIndex], rootInfos, rootIndex));
            }
        }

        return new SceneScanSession(
            new SceneSnapshot
            {
                Sequence = sequence,
                UnixTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Scenes = scenes
            },
            selectedInstanceId,
            pending);
    }

    internal sealed class SceneScanSession
    {
        private readonly int _selectedInstanceId;
        private readonly Queue<SceneScanWorkItem> _pending;

        internal SceneScanSession(
            SceneSnapshot snapshot,
            int selectedInstanceId,
            Queue<SceneScanWorkItem> pending)
        {
            Snapshot = snapshot;
            _selectedInstanceId = selectedInstanceId;
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
                if (gameObject == null)
                {
                    item.Target[item.Index] = UnavailableObject();
                    return;
                }

                var instanceId = gameObject.GetInstanceID();
                var transform = gameObject.transform;
                var childCount = transform.childCount;
                var children = childCount == 0
                    ? Array.Empty<GameObjectInfo>()
                    : new GameObjectInfo[childCount];

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

                    var components = gameObject.GetComponents<Component>();
                    var names = new List<string>(components.Length);
                    foreach (var component in components)
                    {
                        if (component != null)
                        {
                            names.Add(component.GetType().FullName ?? component.GetType().Name);
                        }
                    }
                    componentNames = names.ToArray();
                }

                item.Target[item.Index] = new GameObjectInfo
                {
                    InstanceId = instanceId,
                    Name = gameObject.name ?? string.Empty,
                    ActiveSelf = gameObject.activeSelf,
                    ActiveInHierarchy = gameObject.activeInHierarchy,
                    Layer = layer,
                    Tag = tag,
                    Transform = transformInfo,
                    Components = componentNames,
                    Children = children
                };

                for (var childIndex = 0; childIndex < childCount; childIndex++)
                {
                    try
                    {
                        var child = transform.GetChild(childIndex);
                        if (child == null)
                        {
                            children[childIndex] = UnavailableObject();
                            continue;
                        }

                        _pending.Enqueue(new SceneScanWorkItem(child.gameObject, children, childIndex));
                    }
                    catch (UnityException)
                    {
                        children[childIndex] = UnavailableObject();
                    }
                }
            }
            catch (MissingReferenceException)
            {
                item.Target[item.Index] = UnavailableObject();
            }
            catch (UnityException)
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
        ActiveInHierarchy = false
    };

    private static string ReadTag(GameObject gameObject)
    {
        try
        {
            return gameObject.tag ?? string.Empty;
        }
        catch (UnityException)
        {
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
