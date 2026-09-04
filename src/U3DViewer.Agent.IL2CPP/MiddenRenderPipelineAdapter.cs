using System.Diagnostics;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using U3DViewer.Protocol;
using UnityEngine;
using UnityEngine.Rendering;

namespace U3DViewer.Agent.IL2CPP;

/// <summary>
/// Runtime-only adapter for Sable's custom MiddenRenderPipelineInstance.
///
/// The game exposes both:
///     Render(ScriptableRenderContext, Camera[])
///     Render(ref ScriptableRenderContext, Camera)
///
/// The top-level Camera[] override is the engine-facing custom-SRP entry point, so we patch
/// that reliable boundary. After the game's normal cameras finish, we reuse the same context
/// to invoke Midden's internal single-camera renderer for the disabled U3DViewer Camera.
/// This keeps the free Camera outside Camera.allCameras while still using the game's pipeline.
/// </summary>
internal sealed class MiddenRenderPipelineAdapter : MonoBehaviour
{
    private const float ResolveIntervalSeconds = 1.0f;
    private const float MinCaptureFps = 1f;
    private const float InitialPoseSyncSeconds = 1.0f;
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
    private static readonly FieldInfo? FreeRenderWidthField =
        typeof(SceneCameraController).GetField("_renderWidth", InstancePrivate);
    private static readonly FieldInfo? FreeRenderHeightField =
        typeof(SceneCameraController).GetField("_renderHeight", InstancePrivate);
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
    private static MethodInfo? _topLevelRender;
    private static MethodInfo? _singleCameraRender;
    private static bool _renderingFreeCamera;
    private static bool _firstTopLevelHitLogged;
    private static bool _firstRenderLogged;
    private static bool _installFailureLogged;
    private static bool _streamResizePinnedLogged;
    private static int _poseSyncSourceCameraId;
    private static float _poseSyncUntil;
    private static bool _posePropertiesCopied;
    private static long _topLevelHits;

    private float _nextResolveAt;

    public MiddenRenderPipelineAdapter(IntPtr pointer) : base(pointer)
    {
    }

    internal static bool IsInstalled =>
        _harmony is not null && _topLevelRender is not null && _singleCameraRender is not null;

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

        Harmony? harmony = null;
        try
        {
            var managedType = ResolveManagedPipelineType();
            if (managedType is null)
            {
                LogInstallFailureOnce(
                    "Detected MiddenRenderPipelineInstance, but its generated Assembly-CSharp interop type could not be resolved.");
                return;
            }

            var topLevelRender = ResolveTopLevelRender(managedType);
            var singleRender = ResolveSingleCameraRender(managedType);
            var sceneApply = typeof(SceneCameraController).GetMethod(
                nameof(SceneCameraController.Apply),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var postfixMethod = typeof(MiddenRenderPipelineAdapter).GetMethod(
                nameof(AfterMiddenFrameRender),
                BindingFlags.Static | BindingFlags.NonPublic);
            var streamPrefixMethod = typeof(MiddenRenderPipelineAdapter).GetMethod(
                nameof(StabilizeMiddenStreamSettings),
                BindingFlags.Static | BindingFlags.NonPublic);

            if (topLevelRender is null || singleRender is null || sceneApply is null ||
                postfixMethod is null || streamPrefixMethod is null)
            {
                LogInstallFailureOnce(
                    $"Resolved {managedType.FullName}, but one or more required Midden render/Scene command members were unavailable.");
                return;
            }

            harmony = new Harmony("dev.u3dviewer.agent.il2cpp.midden-frame-render");
            harmony.Patch(topLevelRender, postfix: new HarmonyMethod(postfixMethod));
            harmony.Patch(sceneApply, prefix: new HarmonyMethod(streamPrefixMethod));

            _topLevelRender = topLevelRender;
            _singleCameraRender = singleRender;
            _harmony = harmony;
            _installFailureLogged = false;
            _log?.LogInfo(
                $"[MiddenAdapter] Installed engine-facing render hook on {managedType.FullName}.{topLevelRender}. " +
                $"Isolated free Camera will use {singleRender} after the game's normal camera pass; dynamic Scene transport resize is pinned for Midden stability.");
        }
        catch (Exception ex)
        {
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
            LogInstallFailureOnce($"Could not install Midden top-level render adapter: {ex}");
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

    private static MethodInfo? ResolveTopLevelRender(Type pipelineType)
    {
        foreach (var method in pipelineType.GetMethods(
                     BindingFlags.Instance |
                     BindingFlags.Public |
                     BindingFlags.NonPublic |
                     BindingFlags.DeclaredOnly))
        {
            if (!string.Equals(method.Name, "Render", StringComparison.Ordinal))
            {
                continue;
            }

            var parameters = method.GetParameters();
            if (parameters.Length != 2 || parameters[0].ParameterType != typeof(ScriptableRenderContext))
            {
                continue;
            }

            var second = parameters[1].ParameterType;
            if (second.IsArray || second.Name.Contains("Array", StringComparison.OrdinalIgnoreCase))
            {
                return method;
            }
        }

        return null;
    }

    private static MethodInfo? ResolveSingleCameraRender(Type pipelineType)
    {
        foreach (var method in pipelineType.GetMethods(
                     BindingFlags.Instance |
                     BindingFlags.Public |
                     BindingFlags.NonPublic |
                     BindingFlags.DeclaredOnly))
        {
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

            if (typeof(Camera).IsAssignableFrom(parameters[1].ParameterType))
            {
                return method;
            }
        }

        return null;
    }

    // Sable/IL2CPP becomes unstable when the already-published RenderTexture is destroyed and
    // recreated in response to viewport resizing. Keep the proven transport generation alive and
    // let the Viewer scale it to its viewport. FPS settings still pass through unchanged.
    private static void StabilizeMiddenStreamSettings(
        SceneCameraController __instance,
        ref ViewerCommand command)
    {
        if (command.Kind != ViewerCommandKind.CameraStreamSettings)
        {
            return;
        }

        var width = FreeRenderWidthField?.GetValue(__instance) is int currentWidth
            ? currentWidth
            : 0;
        var height = FreeRenderHeightField?.GetValue(__instance) is int currentHeight
            ? currentHeight
            : 0;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var requestedWidth = (int)command.Z;
        var requestedHeight = (int)command.Value;
        if (requestedWidth == width && requestedHeight == height)
        {
            return;
        }

        command = new ViewerCommand(
            ViewerCommandKind.CameraStreamSettings,
            command.X,
            command.Y,
            width,
            height);

        if (!_streamResizePinnedLogged)
        {
            _streamResizePinnedLogged = true;
            _log?.LogInfo(
                $"[MiddenAdapter] Pinned Scene transport at {width}x{height}; ignored destructive viewport resize request {requestedWidth}x{requestedHeight}. Viewer-side scaling remains active.");
        }
    }

    // The second argument deliberately uses object: generated IL2CPP wrappers may expose the
    // Camera[] parameter as an Il2CppReferenceArray<Camera> rather than a CLR Camera[]. We do
    // not need to mutate it; the context is the only input required for isolated rendering.
    private static void AfterMiddenFrameRender(
        object __instance,
        ScriptableRenderContext __0,
        object __1)
    {
        _topLevelHits++;
        if (!_firstTopLevelHitLogged)
        {
            _firstTopLevelHitLogged = true;
            _log?.LogInfo(
                $"[MiddenAdapter] Engine-facing Midden Render hook is active. First hit #{_topLevelHits}; camera-array managed type={__1?.GetType().FullName ?? "<null>"}.");
        }

        if (_renderingFreeCamera)
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
        if (sourceCameraId == 0)
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

        var sourceCamera = FindSourceCamera(sourceCameraId, freeCamera);
        if (_poseSyncSourceCameraId != sourceCameraId)
        {
            _poseSyncSourceCameraId = sourceCameraId;
            _poseSyncUntil = now + InitialPoseSyncSeconds;
            _posePropertiesCopied = false;
        }

        if (sourceCamera is not null && now <= _poseSyncUntil)
        {
            if (!_posePropertiesCopied)
            {
                var target = renderTexture;
                freeCamera.CopyFrom(sourceCamera);
                freeCamera.enabled = false;
                freeCamera.targetTexture = target;
                _posePropertiesCopied = true;
                _log?.LogInfo(
                    $"[MiddenAdapter] Initial free-Camera pose sync started from {sourceCamera.name}#{sourceCameraId} for {InitialPoseSyncSeconds:0.0}s.");
            }

            freeCamera.transform.position = sourceCamera.transform.position;
            freeCamera.transform.rotation = sourceCamera.transform.rotation;
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            ApplyFollowTransformMethod?.Invoke(controller, new object[] { false });

            freeCamera.enabled = false;
            freeCamera.targetTexture = renderTexture;

            _renderingFreeCamera = true;
            var invokeArgs = new object[] { __0, freeCamera };
            _singleCameraRender?.Invoke(__instance, invokeArgs);
            if (invokeArgs[0] is ScriptableRenderContext updatedContext)
            {
                __0 = updatedContext;
            }

            // Midden's per-camera renderer records SRP commands into the supplied context.
            // Submit before the transport blit so NativeBridge never copies the previous frame.
            __0.Submit();
            Graphics.Blit(renderTexture, transportTexture);
            GL.IssuePluginEvent(renderEvent, copyEventId);

            var elapsedMs =
                (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            FreeRecordRenderTimingMethod?.Invoke(controller, new object[] { elapsedMs });
            FreeRecordRenderFrameMethod?.Invoke(controller, new object[] { now });
            FreeRenderStatusField?.SetValue(
                controller,
                "Midden custom SRP free Scene Camera rendered after the engine-facing camera pass and published to NativeBridge.");

            if (!_firstRenderLogged)
            {
                _firstRenderLogged = true;
                _log?.LogInfo(
                    $"[MiddenAdapter] First isolated free Scene Camera frame rendered from top-level hook; " +
                    $"source={sourceCameraId}, free={freeCamera.GetInstanceID()}, target={renderTexture.width}x{renderTexture.height}, pos={FormatVector(freeCamera.transform.position)}.");
            }
        }
        catch (TargetInvocationException ex)
        {
            var inner = ex.InnerException ?? ex;
            FreeRenderStatusField?.SetValue(controller, $"Midden isolated render failed: {inner.Message}");
            _log?.LogWarning($"[MiddenAdapter] Isolated render failed: {inner}");
        }
        catch (Exception ex)
        {
            FreeRenderStatusField?.SetValue(controller, $"Midden isolated render failed: {ex.Message}");
            _log?.LogWarning($"[MiddenAdapter] Isolated render failed: {ex}");
        }
        finally
        {
            _renderingFreeCamera = false;
        }
    }

    private static Camera? FindSourceCamera(int instanceId, Camera freeCamera)
    {
        var cameras = Camera.allCameras;
        for (var index = 0; index < cameras.Length; index++)
        {
            var candidate = cameras[index];
            if (candidate is not null && candidate != freeCamera &&
                candidate.GetInstanceID() == instanceId && candidate.gameObject.activeInHierarchy)
            {
                return candidate;
            }
        }

        return null;
    }

    private static string FormatVector(Vector3 value) =>
        $"({value.x:0.##},{value.y:0.##},{value.z:0.##})";

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
        _topLevelRender = null;
        _singleCameraRender = null;
        _renderingFreeCamera = false;
        _firstTopLevelHitLogged = false;
        _firstRenderLogged = false;
        _installFailureLogged = false;
        _streamResizePinnedLogged = false;
        _poseSyncSourceCameraId = 0;
        _poseSyncUntil = 0f;
        _posePropertiesCopied = false;
        _topLevelHits = 0;

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
