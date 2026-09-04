using System.Reflection;
using System.Text;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace U3DViewer.Agent.IL2CPP;

/// <summary>
/// Focused diagnostics for the IL2CPP Scene path plus one narrow compatibility guard:
/// custom SRPs that do not expose URP RenderSingleCamera must not receive an enabled
/// __U3DViewerCamera in their normal camera list, because that perturbs the game render.
/// </summary>
internal sealed class SceneDiagnosticsBehaviour : MonoBehaviour
{
    private const float SummaryIntervalSeconds = 2.5f;
    private const BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;

    private static readonly FieldInfo? SceneCameraField =
        typeof(RuntimeBehaviour).GetField("_sceneCamera", StaticPrivate);
    private static readonly FieldInfo? SourceCaptureField =
        typeof(RuntimeBehaviour).GetField("_sourceCapture", StaticPrivate);
    private static readonly FieldInfo? FreeCameraField =
        typeof(SceneCameraController).GetField("_camera", InstancePrivate);
    private static readonly FieldInfo? FreeViewerVisibleField =
        typeof(SceneCameraController).GetField("_viewerVisible", InstancePrivate);
    private static readonly FieldInfo? FreeSelectedSourceIdField =
        typeof(SceneCameraController).GetField("_selectedSourceCameraInstanceId", InstancePrivate);
    private static readonly FieldInfo? FreeEffectiveSourceIdField =
        typeof(SceneCameraController).GetField("_effectiveSourceCameraInstanceId", InstancePrivate);
    private static readonly FieldInfo? FreeRenderStatusField =
        typeof(SceneCameraController).GetField("_renderStatus", InstancePrivate);

    private static ManualLogSource? _sharedLog;
    private Harmony? _guardHarmony;
    private float _nextSummaryAt;
    private string _lastPipeline = string.Empty;
    private string _lastInventory = string.Empty;
    private int _endCameraCallbacks;
    private int _selectedSourceCallbacks;
    private readonly System.Action<ScriptableRenderContext, Camera> _managedEndCameraRenderingHandler;
    private readonly Il2CppSystem.Action<ScriptableRenderContext, Camera> _endCameraRenderingHandler;

    public SceneDiagnosticsBehaviour(IntPtr pointer) : base(pointer)
    {
        _managedEndCameraRenderingHandler = OnEndCameraRendering;
        _endCameraRenderingHandler =
            (Il2CppSystem.Action<ScriptableRenderContext, Camera>)_managedEndCameraRenderingHandler;
        RenderPipelineManager.endCameraRendering += _endCameraRenderingHandler;
    }

    public void Start()
    {
        _sharedLog = BepInEx.Logging.Logger.CreateLogSource("U3DViewer SceneDiag");
        _nextSummaryAt = 0f;

        try
        {
            _guardHarmony = new Harmony("dev.u3dviewer.agent.il2cpp.custom-srp-camera-guard");
            _guardHarmony.CreateClassProcessor(typeof(CustomSrpCameraGuardPatch)).Patch();
            _sharedLog.LogInfo("[SceneDiag] Custom-SRP camera guard installed. It only suppresses the free Camera from the game camera list when no isolated URP RenderSingleCamera path exists.");
        }
        catch (Exception ex)
        {
            _sharedLog.LogWarning($"[SceneDiag] Could not install custom-SRP camera guard: {ex}");
        }

        LogPipeline(force: true);
        LogCameraInventory(force: true);
        LogSummary();
    }

    public void Update()
    {
        var now = Time.unscaledTime;
        if (now < _nextSummaryAt)
        {
            return;
        }

        _nextSummaryAt = now + SummaryIntervalSeconds;
        LogPipeline(force: false);
        LogCameraInventory(force: false);
        LogSummary();
    }

    private void OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
    {
        if (camera is null)
        {
            return;
        }

        _endCameraCallbacks++;
        try
        {
            var controller = SceneCameraField?.GetValue(null) as SceneCameraController;
            var selected = controller is null ? 0 : ReadInt(FreeEffectiveSourceIdField, controller);
            if (selected != 0 && camera.GetInstanceID() == selected)
            {
                _selectedSourceCallbacks++;
            }
        }
        catch
        {
        }
    }

    private static bool HasIsolatedUrpPath()
    {
        try
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(
                    "UnityEngine.Rendering.Universal.UniversalRenderPipeline",
                    throwOnError: false);
                if (type is null)
                {
                    continue;
                }

                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                for (var index = 0; index < methods.Length; index++)
                {
                    if (string.Equals(methods[index].Name, "RenderSingleCamera", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool IsCustomSrpWithoutIsolatedCameraPath() =>
        RenderPipelineManager.currentPipeline is not null && !HasIsolatedUrpPath();

    private void LogPipeline(bool force)
    {
        string pipeline;
        try
        {
            var current = RenderPipelineManager.currentPipeline;
            pipeline = current is null
                ? "<Built-in/null>"
                : $"{current.GetType().FullName ?? current.GetType().Name} [asm={current.GetType().Assembly.GetName().Name}]";
        }
        catch (Exception ex)
        {
            pipeline = $"<error:{ex.GetType().Name}:{ex.Message}>";
        }

        var assemblies = new StringBuilder();
        try
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = assembly.GetName().Name ?? string.Empty;
                if (name.IndexOf("RenderPipeline", StringComparison.OrdinalIgnoreCase) < 0 &&
                    name.IndexOf("Universal", StringComparison.OrdinalIgnoreCase) < 0 &&
                    name.IndexOf("HighDefinition", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                if (assemblies.Length > 0)
                {
                    assemblies.Append(',');
                }
                assemblies.Append(name);
            }
        }
        catch
        {
        }

        var state = $"pipeline={pipeline}; isolatedURP={HasIsolatedUrpPath()}; renderAssemblies={assemblies}";
        if (!force && string.Equals(state, _lastPipeline, StringComparison.Ordinal))
        {
            return;
        }

        _lastPipeline = state;
        _sharedLog?.LogInfo($"[SceneDiag] Pipeline: {state}");
    }

    private void LogCameraInventory(bool force)
    {
        var inventory = BuildCameraInventory();
        if (!force && string.Equals(inventory, _lastInventory, StringComparison.Ordinal))
        {
            return;
        }

        _lastInventory = inventory;
        _sharedLog?.LogInfo($"[SceneDiag] Cameras: {inventory}");
    }

    private static string BuildCameraInventory()
    {
        Camera[] cameras;
        try
        {
            cameras = Camera.allCameras ?? Array.Empty<Camera>();
        }
        catch (Exception ex)
        {
            return $"<Camera.allCameras failed:{ex.GetType().Name}:{ex.Message}>";
        }

        if (cameras.Length == 0)
        {
            return "0 cameras";
        }

        var builder = new StringBuilder();
        builder.Append(cameras.Length).Append(" camera(s): ");
        for (var index = 0; index < cameras.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(" | ");
            }
            builder.Append(DescribeCamera(cameras[index]));
        }
        return builder.ToString();
    }

    private static string DescribeCamera(Camera? camera)
    {
        if (camera is null)
        {
            return "<null>";
        }

        try
        {
            return
                $"{camera.name}#{camera.GetInstanceID()} enabled={camera.enabled} depth={camera.depth:0.###} " +
                $"target={(camera.targetTexture is null ? "screen" : camera.targetTexture.name)} " +
                $"pos=({camera.transform.position.x:0.##},{camera.transform.position.y:0.##},{camera.transform.position.z:0.##}) " +
                $"components=[{DescribeComponents(camera.gameObject)}]";
        }
        catch (Exception ex)
        {
            return $"<camera wrapper error:{ex.GetType().Name}:{ex.Message}>";
        }
    }

    private static string DescribeComponents(GameObject gameObject)
    {
        try
        {
            var components = gameObject.GetComponents<Component>();
            var builder = new StringBuilder();
            for (var index = 0; index < components.Length; index++)
            {
                var component = components[index];
                if (component is null)
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append(',');
                }

                var type = component.GetType();
                builder.Append(type.FullName ?? type.Name);
                if (component is Behaviour behaviour)
                {
                    builder.Append("(enabled=").Append(behaviour.enabled).Append(')');
                }
            }
            return builder.ToString();
        }
        catch (Exception ex)
        {
            return $"<components error:{ex.GetType().Name}:{ex.Message}>";
        }
    }

    private void LogSummary()
    {
        var controller = SceneCameraField?.GetValue(null) as SceneCameraController;
        var direct = SourceCaptureField?.GetValue(null) as SourceCameraCaptureController;
        var camera = controller is null ? null : FreeCameraField?.GetValue(controller) as Camera;
        var selected = controller is null ? 0 : ReadInt(FreeSelectedSourceIdField, controller);
        var effective = controller is null ? 0 : ReadInt(FreeEffectiveSourceIdField, controller);
        var visible = controller is not null && ReadBool(FreeViewerVisibleField, controller);
        var status = controller is null ? string.Empty : ReadString(FreeRenderStatusField, controller);
        var position = camera is null
            ? "<null>"
            : $"({camera.transform.position.x:0.##},{camera.transform.position.y:0.##},{camera.transform.position.z:0.##})";

        _sharedLog?.LogInfo(
            $"[SceneDiag] Summary: customSRPGuard={IsCustomSrpWithoutIsolatedCameraPath()}; " +
            $"viewerVisible={visible}; freeCameraEnabled={camera?.enabled}; freePos={position}; " +
            $"selectedSource={selected}; effectiveSource={effective}; directCapture={direct?.Enabled}; " +
            $"endCameraRendering/{SummaryIntervalSeconds:0.0}s={_endCameraCallbacks}; selectedMatches={_selectedSourceCallbacks}; " +
            $"owner={SceneTransportCoordinator.Owner}; epoch={SceneTransportCoordinator.Epoch}; status={status}");

        _endCameraCallbacks = 0;
        _selectedSourceCallbacks = 0;
    }

    private static bool ReadBool(FieldInfo? field, object instance)
    {
        try
        {
            return field?.GetValue(instance) is bool value && value;
        }
        catch
        {
            return false;
        }
    }

    private static int ReadInt(FieldInfo? field, object instance)
    {
        try
        {
            return field?.GetValue(instance) is int value ? value : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string ReadString(FieldInfo? field, object instance)
    {
        try
        {
            return field?.GetValue(instance) as string ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public void OnDestroy()
    {
        try
        {
            RenderPipelineManager.endCameraRendering -= _endCameraRenderingHandler;
        }
        catch
        {
        }

        try
        {
            _guardHarmony?.UnpatchSelf();
        }
        catch
        {
        }

        _guardHarmony = null;
        _sharedLog = null;
    }

    [HarmonyPatch(typeof(SceneCameraController), nameof(SceneCameraController.TickRender))]
    private static class CustomSrpCameraGuardPatch
    {
        private static readonly FieldInfo? ViewerVisibleField =
            typeof(SceneCameraController).GetField("_viewerVisible", InstancePrivate);

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static void SuppressCustomSrpCamera(SceneCameraController __instance, out bool __state)
        {
            __state = false;
            if (!IsCustomSrpWithoutIsolatedCameraPath() || ViewerVisibleField is null)
            {
                return;
            }

            try
            {
                if (ViewerVisibleField.GetValue(__instance) is bool visible && visible)
                {
                    ViewerVisibleField.SetValue(__instance, false);
                    __state = true;
                }
            }
            catch
            {
            }
        }

        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void RestoreViewerVisibility(SceneCameraController __instance, bool __state)
        {
            if (!__state || ViewerVisibleField is null)
            {
                return;
            }

            try
            {
                ViewerVisibleField.SetValue(__instance, true);
            }
            catch
            {
            }
        }
    }
}
