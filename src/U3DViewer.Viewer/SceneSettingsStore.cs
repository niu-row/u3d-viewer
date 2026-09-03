using System.Text.Json;
using U3DViewer.Protocol;

namespace U3DViewer.Viewer;

internal sealed class SceneSettingsProfile
{
    public int Schema { get; set; } = 1;
    public float FieldOfView { get; set; } = 60f;
    public float NearClip { get; set; } = 0.001f;
    public float FarClip { get; set; } = 10000f;
    public float OrthographicSize { get; set; } = 5f;
    public float IdleFps { get; set; } = 15f;
    public float InteractiveFps { get; set; } = 30f;
    public bool AutoViewport { get; set; } = true;
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public SceneCullingMode CullingMode { get; set; } = SceneCullingMode.MainCamera;
    public int CullingMask { get; set; } = -1;
}

internal static class SceneSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static SceneSettingsProfile? Load(string executablePath)
    {
        try
        {
            var path = ViewerPaths.GetSceneSettingsPath(executablePath);
            if (!File.Exists(path))
            {
                return null;
            }

            var profile = JsonSerializer.Deserialize<SceneSettingsProfile>(File.ReadAllText(path), JsonOptions);
            return profile?.Schema == 1 ? profile : null;
        }
        catch (Exception ex)
        {
            ViewerLog.Warning($"Could not load Scene settings for '{executablePath}': {ex.Message}");
            return null;
        }
    }

    public static void Save(string executablePath, SceneSettingsProfile profile)
    {
        try
        {
            var path = ViewerPaths.GetSceneSettingsPath(executablePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(profile, JsonOptions));
        }
        catch (Exception ex)
        {
            ViewerLog.Warning($"Could not save Scene settings for '{executablePath}': {ex.Message}");
        }
    }
}
