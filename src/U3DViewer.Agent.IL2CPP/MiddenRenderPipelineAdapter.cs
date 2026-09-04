using System.Diagnostics;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using UnityEngine.Rendering;

namespace U3DViewer.Agent.IL2CPP;

/// <summary>
/// Runtime-only adapter for Sable's custom MiddenRenderPipelineInstance.
///
/// Midden exposes an internal per-camera entry point:
///     Render(ref ScriptableRenderContext, Camera)
///
/// We patch that entry point after the real source Camera is rendered and invoke it once more
/// for the disabled U3DViewer Camera with the exact same ScriptableRenderContext. This keeps the
/// free Camera out of the game's normal Camera list (avoiding the observed flicker) while still
/// rendering it through the game's own custom pipeline.
/// </summary>
internal sealed class MiddenRenderPipelineAdapter : MonoBehaviour
{
    private const float ResolveIntervalSeconds = 1.0f;
    private const float MinCaptureFps = 1f;
    private const string PipelineTypeName = "MiddenRenderPipelineInstance";
    private const BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;

    private static readonly FieldInfo? SceneCameraField =
        typeof(RuntimeBehaviour).GetField("_sceneCamera", StaticPrivate);
    private static readonly FieldInfo? SourceCaptureField =
        typeof(RuntimeBehaviour).GetField("_sourceCapture", StaticPrivate);

    private static readonly FieldInfo? FreeCameraField =
        typeof(SceneCameraController).GetField("_camera", InstancePrivate);
    private static readonly FieldInfo? FreeRenderTextureField =
        typeof(SceneCameraController).GetField("_renderTexture", InstancePrivate);
    private static readonly FieldInfo? FreeTransportTextureField =
        typeof(SceneCameraController).GetField("_transportTexture", InstancePrivate);
    private static readonly FieldInfo? FreeViewerVisibleField =
        typeof(SceneCameraController).GetField("_viewerVisible", InstancePrivate);
    private static readonly FieldInfo? FreeBridgeReadyField =
        typeof(SceneCameraController).GetField("_bridgeReady", InstancePrivate);
    private static readonly FieldInfo? FreeRenderEventField =
        typeof(SceneCameraController).GetField("_renderEvent", InstancePrivate);
    private static readonly FieldInfo? FreeCopyEventIdField =
        typeof(SceneCameraController).GetField("_copyEventId", InstancePrivate);
    private static readonly FieldInfo? FreeEffectiveSourceCameraIdField =
        typeof(SceneCameraController).GetField("_effectiveSourceCameraInstanceId", InstancePrivate);
    private static readonly FieldInfo? FreeNextRenderAtField =
        typeof(SceneCameraController).GetField("_nextRenderAt", InstancePrivate);
    private static readonly FieldInfo? FreeInteractiveUntilField =
        typeof(SceneCameraController).GetField("_interactiveUntil", InstancePrivate);
    private static readonly FieldInfo? FreeIdleFpsField =
        typeof(SceneCameraController).GetField("_idleFps", InstancePrivate);
    private static readonly FieldInfo? FreeInteractiveFpsField =
        typeof(SceneCameraController).GetField("_interactiveFps", InstancePrivate);
    private static readonly FieldInfo? FreeRenderStatusField =
        typeof(SceneCameraController).GetField("_renderStatus", InstancePrivate);

    private static readonly MethodInfo? ApplyFollowTransformMethod =
        typeof(SceneCameraController).GetMethod("ApplyFollowTransform", InstancePrivate);
    private static readonly MethodInfo? FreeRecordRenderTimingMethod =
        typeof(SceneCameraController).GetMethod("RecordRenderTiming", InstancePrivate);
    private static readonly MethodInfo? FreeRecordRenderFrameMethod =
        typeof(SceneCameraController).GetMethod("RecordRenderFrame", InstancePrivate);

    private static ManualLogSource? _log;
    private static Harmony? _harmony;
    private static MethodInfo? _singleCameraRender;
    private static bool _renderingFreeCamera;
    private static bool _firstRenderLogged;
    private static bool _installFailureLogged;

    private float _nextResolveAt;

    public MiddenRenderPipelineAdapter(IntPtr pointer) : base(pointer)
    {
    }

    internal static bool IsInstalled => _harmony is not null && _singleCameraRender is not null;

    public void Start()
    {
        _log = BepInEx.Logging.Logger.CreateLogSource("U3DViewer MiddenAdapter");
        _nextResolveAt = 0f;
    }

    public void Update()
    {
        if (IsInstalled)
        {
            return;
        }

        var now = Time.unscaledTime;
        if (now < _nextResolveAt)
        {
            return;
        }
        _nextResolveAt = now + ResolveIntervalSeconds;

        TryInstall();
    }

    private static void TryInstall()
    {
        var pipeline = RenderPipelineManager.currentPipeline;
        if (pipeline is null)
        {
            return;
        }

        string il2cppTypeName;
        try
        {
            var il2cppType = pipeline.GetIl2CppType();
            il2cppTypeName = il2cppType?.FullName ?? il2cppType?.Name ?? string.Empty;
        }
        catch
        {
            return;
        }

        if (!string.Equals(il2cppTypeName, PipelineTypeName, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            var managedType = ResolveManagedPipelineType();
            if (managedType is null)
            {
                LogInstallFailureOnce(
                    "Detected MiddenRenderPipelineInstance, but its generated Assembly-CSharp interop type could not be resolved.");
                return;
            }

            var singleRender = ResolveSingleCameraRender(managedType);
            if (singleRender is null)
            {
                LogInstallFailureOnce(
                    $"Resolved {managedType.FullName}, but Render(ref ScriptableRenderContext, Camera) was not found on its managed interop wrapper.");
                return;
            }

            var postfixMethod = typeof(MiddenRenderPipelineAdapter).GetMethod(
                nameof(AfterMiddenCameraRender),
                BindingFlags.Static | BindingFlags.NonPublic);
            if (postfixMethod is null)
            {
                LogInstallFailureOnce("Midden adapter postfix method could not be resolved.");
                return;
            }

            var harmony = new Harmony("dev.u3dviewer.agent.il2cpp.midden-single-camera");
            harmony.Patch(singleRender, postfix: new HarmonyMethod(postfixMethod));

            _singleCameraRender = singleRender;
            _harmony = harmony;
            _installFailureLogged = false;
            _log?.LogInfo(
                $"[MiddenAdapter] Installed isolated free-Camera render hook on {managedType.FullName}.{singleRender.Name}. " +
                "The U3DViewer Camera remains disabled and will be rendered immediately after the selected source Camera using the same ScriptableRenderContext.");
        }
        catch (Exception ex)
        {
            LogInstallFailureOnce($"Could not install Midden single-Camera render adapter: {ex}");
        }
    }

    private static Type? ResolveManagedPipelineType()
    {
        static Type? FindLoaded()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = assembly.GetType(PipelineTypeName, throwOnError: false, ignoreCase: false);
                    if (type is not null)
                    {
                        return type;
                    }
                }
                catch
                {
                }
            }
            return null;
        }

        var existing = FindLoaded();
        if (existing is not null)
        {
            return existing;
        }

        try
        {
            Assembly.Load("Assembly-CSharp");
        }
        catch
        {
        }

        return FindLoaded();
    }

    private static MethodInfo? ResolveSingleCameraRender(Type pipelineType)
    {
        var methods = pipelineType.GetMethods(
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly);

        for (var index = 0; index < methods.Length; index++)
        {
            var method = methods[index];
            if (!string.Equals(method.Name, "Render", StringComparison.Ordinal))
            {
                continue;
            }

            var parameters = method.GetParameters();
            if (parameters.Length != 2)
            {
                continue;
            }

            var contextType = parameters[0].ParameterType;
            if (!contextType.IsByRef || contextType.GetElementType() != typeof(ScriptableRenderContext))
            {
                continue;
            }

            if (!typeof(Camera).IsAssignableFrom(parameters[1].ParameterType))
            {
                continue;
            }

            return method;
        }

        return null;
    }

    // This exact signature matches MiddenRenderPipelineInstance.Render(ref ScriptableRenderContext, Camera).
    // The recursion guard is necessary because the adapter invokes the same method for the free Camera.
    private static void AfterMiddenCameraRender(
        object __instance,
        ref ScriptableRenderContext __0,
        Camera __1)
    {
        if (_renderingFreeCamera || __1 is null)
        {
            return;
        }

        var directController = SourceCaptureField?.GetValue(null) as SourceCameraCaptureController;
        if (directController?.Enabled == true)
        {
            return;
        }

        var controller = SceneCameraField?.GetValue(null) as SceneCameraController;
        if (controller is null || !SceneTransportCoordinator.IsOwner(SceneTransportOwner.FreeCamera))
        {
            return;
        }

        var sourceCameraId = FreeEffectiveSourceCameraIdField?.GetValue(controller) is int sourceId
            ? sourceId
            : 0;
        if (sourceCameraId == 0 || __1.GetInstanceID() != sourceCameraId)
        {
            return;
        }

        var freeCamera = FreeCameraField?.GetValue(controller) as Camera;
        var renderTexture = FreeRenderTextureField?.GetValue(controller) as RenderTexture;
        var transportTexture = FreeTransportTextureField?.GetValue(controller) as RenderTexture;
        var viewerVisible = FreeViewerVisibleField?.GetValue(controller) is bool visible && visible;
        var bridgeReady = FreeBridgeReadyField?.GetValue(controller) is bool ready && ready;
        if (freeCamera is null || renderTexture is null || transportTexture is null ||
            !viewerVisible || !bridgeReady)
        {
            return;
        }

        var renderEvent = FreeRenderEventField?.GetValue(controller) is IntPtr eventPtr
            ? eventPtr
            : IntPtr.Zero;
        var copyEventId = FreeCopyEventIdField?.GetValue(controller) is int eventId
            ? eventId
            : 0;
        if (renderEvent == IntPtr.Zero)
        {
            return;
        }

        var now = Time.unscaledTime;
        var nextRenderAt = FreeNextRenderAtField?.GetValue(controller) is float next
            ? next
            : 0f;
        if (now < nextRenderAt)
        {
            return;
        }

        var interactiveUntil = FreeInteractiveUntilField?.GetValue(controller) is float interactive
            ? interactive
            : 0f;
        var idleFps = FreeIdleFpsField?.GetValue(controller) is float idle
            ? idle
            : 15f;
        var interactiveFps = FreeInteractiveFpsField?.GetValue(controller) is float active
            ? active
            : 30f;
        var fps = now < interactiveUntil ? interactiveFps : idleFps;
        FreeNextRenderAtField?.SetValue(controller, now + 1f / Mathf.Max(MinCaptureFps, fps));

        var started = Stopwatch.GetTimestamp();
        try
        {
            ApplyFollowTransformMethod?.Invoke(controller, new object[] { false });

            // Keep the free Camera permanently outside Camera.allCameras / the game's normal list.
            freeCamera.enabled = false;
            freeCamera.targetTexture = renderTexture;

            _renderingFreeCamera = true;
            var invokeArgs = new object[] { __0, freeCamera };
            _singleCameraRender?.Invoke(__instance, invokeArgs);
            if (invokeArgs[0] is ScriptableRenderContext updatedContext)
            {
                __0 = updatedContext;
            }

            // Publish exactly the texture NativeBridge owns. This mirrors the proven direct-capture path.
            Graphics.Blit(renderTexture, transportTexture);
            GL.IssuePluginEvent(renderEvent, copyEventId);

            var elapsedMs =
                (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            FreeRecordRenderTimingMethod?.Invoke(controller, new object[] { elapsedMs });
            FreeRecordRenderFrameMethod?.Invoke(controller, new object[] { now });
            FreeRenderStatusField?.SetValue(
                controller,
                "Midden custom SRP free Scene Camera rendered through Render(ref ScriptableRenderContext, Camera) and published to NativeBridge.");

            if (!_firstRenderLogged)
            {
                _firstRenderLogged = true;
                _log?.LogInfo(
                    $"[MiddenAdapter] First isolated free Scene Camera frame rendered after source Camera {sourceCameraId}; " +
                    $"free={freeCamera.GetInstanceID()} target={renderTexture.width}x{renderTexture.height}.");
            }
        }
        catch (TargetInvocationException ex)
        {
            var inner = ex.InnerException ?? ex;
            FreeRenderStatusField?.SetValue(controller, $"Midden single-Camera render failed: {inner.Message}");
            _log?.LogWarning($"[MiddenAdapter] Single-Camera render failed: {inner}");
        }
        catch (Exception ex)
        {
            FreeRenderStatusField?.SetValue(controller, $"Midden single-Camera render failed: {ex.Message}");
            _log?.LogWarning($"[MiddenAdapter] Single-Camera render failed: {ex}");
        }
        finally
        {
            _renderingFreeCamera = false;
        }
    }

    private static void LogInstallFailureOnce(string message)
    {
        if (_installFailureLogged)
        {
            return;
        }

        _installFailureLogged = true;
        _log?.LogWarning($"[MiddenAdapter] {message}");
    }

    public void OnDestroy()
    {
        var harmony = _harmony;
        _harmony = null;
        _singleCameraRender = null;
        _renderingFreeCamera = false;
        _firstRenderLogged = false;
        _installFailureLogged = false;

        if (harmony is not null)
        {
            try
            {
                harmony.UnpatchSelf();
            }
            catch
            {
            }
        }

        _log = null;
    }
}
