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
    public bool FollowMainCameraPosition { get; set; }
    public bool FollowMainCameraRotation { get; set; }
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
            if (profile?.Schema != 1)
            {
                return null;
            }

            Normalize(profile);
            return profile;
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
            Normalize(profile);
            var path = ViewerPaths.GetSceneSettingsPath(executablePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(profile, JsonOptions));
        }
        catch (Exception ex)
        {
            ViewerLog.Warning($"Could not save Scene settings for '{executablePath}': {ex.Message}");
        }
    }

    private static void Normalize(SceneSettingsProfile profile)
    {
        profile.Schema = 1;
        profile.FieldOfView = ClampFinite(profile.FieldOfView, 60f, 1f, 179f);
        profile.NearClip = float.IsFinite(profile.NearClip) ? profile.NearClip : 0.001f;
        profile.FarClip = float.IsFinite(profile.FarClip) && profile.FarClip > profile.NearClip + 0.0001f
            ? profile.FarClip
            : Math.Max(10000f, profile.NearClip + 1f);
        profile.OrthographicSize = ClampFinite(profile.OrthographicSize, 5f, 0.001f, float.MaxValue);
        profile.IdleFps = ClampFinite(profile.IdleFps, 15f, 1f, 120f);
        profile.InteractiveFps = ClampFinite(profile.InteractiveFps, 30f, 1f, 120f);
        profile.Width = Math.Clamp(profile.Width, 64, 4096);
        profile.Height = Math.Clamp(profile.Height, 64, 4096);
        if (!Enum.IsDefined(typeof(SceneCullingMode), profile.CullingMode))
        {
            profile.CullingMode = SceneCullingMode.MainCamera;
        }
    }

    private static float ClampFinite(float value, float fallback, float minimum, float maximum) =>
        float.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
}
