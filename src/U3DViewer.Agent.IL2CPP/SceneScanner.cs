using Il2CppInterop.Runtime;
using U3DViewer.Protocol;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace U3DViewer.Agent.IL2CPP;

internal static class SceneScanner
{
    public static SceneSnapshot Capture(long sequence)
    {
        var scenes = new SceneInfo[SceneManager.sceneCount];

        for (var i = 0; i < SceneManager.sceneCount; i++)
        {
            var scene = SceneManager.GetSceneAt(i);
            var roots = scene.GetRootGameObjects();
            var rootInfos = new GameObjectInfo[roots.Length];

            for (var rootIndex = 0; rootIndex < roots.Length; rootIndex++)
            {
                rootInfos[rootIndex] = CaptureGameObject(roots[rootIndex]);
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

    private static GameObjectInfo CaptureGameObject(GameObject gameObject)
    {
        var transform = gameObject.transform;
        var children = new GameObjectInfo[transform.childCount];

        for (var i = 0; i < transform.childCount; i++)
        {
            children[i] = CaptureGameObject(transform.GetChild(i).gameObject);
        }

        var components = gameObject.GetComponents(Il2CppType.Of<Component>());
        var componentNames = new string[components.Length];
        for (var i = 0; i < components.Length; i++)
        {
            var component = components[i];
            componentNames[i] = component is null
                ? "<missing>"
                : component.GetType().FullName ?? component.GetType().Name;
        }

        return new GameObjectInfo
        {
            InstanceId = gameObject.GetInstanceID(),
            Name = gameObject.name ?? string.Empty,
            ActiveSelf = gameObject.activeSelf,
            ActiveInHierarchy = gameObject.activeInHierarchy,
            Layer = gameObject.layer,
            Tag = ReadTag(gameObject),
            Transform = new TransformInfo
            {
                Position = ToInfo(transform.position),
                LocalPosition = ToInfo(transform.localPosition),
                EulerAngles = ToInfo(transform.eulerAngles),
                LocalScale = ToInfo(transform.localScale)
            },
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
