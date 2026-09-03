using U3DViewer.Protocol;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace U3DViewer.Agent.Mono;

internal static class SceneScanner
{
    public static SceneSnapshot Capture(long sequence, int selectedInstanceId)
    {
        var scenes = new SceneInfo[SceneManager.sceneCount];

        for (var i = 0; i < SceneManager.sceneCount; i++)
        {
            var scene = SceneManager.GetSceneAt(i);
            var roots = scene.GetRootGameObjects();
            var rootInfos = new GameObjectInfo[roots.Length];

            for (var rootIndex = 0; rootIndex < roots.Length; rootIndex++)
            {
                rootInfos[rootIndex] = CaptureGameObject(roots[rootIndex], selectedInstanceId);
            }

            scenes[i] = new SceneInfo
            {
                BuildIndex = scene.buildIndex,
                Name = scene.name ?? string.Empty,
                IsLoaded = scene.isLoaded,
                Roots = rootInfos
            };
        }

        return new SceneSnapshot
        {
            Sequence = sequence,
            UnixTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Scenes = scenes
        };
    }

    private static GameObjectInfo CaptureGameObject(GameObject gameObject, int selectedInstanceId)
    {
        var instanceId = gameObject.GetInstanceID();
        var transform = gameObject.transform;
        var children = new GameObjectInfo[transform.childCount];

        for (var i = 0; i < transform.childCount; i++)
        {
            children[i] = CaptureGameObject(transform.GetChild(i).gameObject, selectedInstanceId);
        }

        var isSelected = instanceId == selectedInstanceId;
        var transformInfo = new TransformInfo();
        var componentNames = Array.Empty<string>();
        var layer = 0;
        var tag = string.Empty;

        if (isSelected)
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

        return new GameObjectInfo
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
    }

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
