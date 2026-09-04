using System.Collections;
using System.Diagnostics;
using System.Reflection;
using BepInEx.Unity.IL2CPP.Utils;
using U3DViewer.Protocol;
using UnityEngine;
using UnityEngine.Rendering;

namespace U3DViewer.Agent.IL2CPP;

/// <summary>
/// SRP transport support. Direct-source mode falls back to the completed Game View when
/// camera callbacks are unavailable. For URP, the free Scene Camera is rendered explicitly
/// through UniversalRenderPipeline.RenderSingleCamera instead of being inserted into the
/// game's normal Camera list.
/// </summary>
internal sealed class SourceCaptureEndOfFrameFallback : MonoBehaviour
{
    private const float MinCaptureFps = 1f;

    private static readonly BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;

    private static readonly FieldInfo? SourceCaptureField =
        typeof(RuntimeBehaviour).GetField("_sourceCapture", StaticPrivate);
    private static readonly FieldInfo? SceneCameraField =
        typeof(RuntimeBehaviour).GetField("_sceneCamera", StaticPrivate);

    private static readonly FieldInfo? RenderTextureField =
        typeof(SourceCameraCaptureController).GetField("_renderTexture", InstancePrivate);
    private static readonly FieldInfo? RenderEventField =
        typeof(SourceCameraCaptureController).GetField("_renderEvent", InstancePrivate);
    private static readonly FieldInfo? CopyEventIdField =
        typeof(SourceCameraCaptureController).GetField("_copyEventId", InstancePrivate);
    private static readonly FieldInfo? NextCaptureAtField =
        typeof(SourceCameraCaptureController).GetField("_nextCaptureAt", InstancePrivate);
    private static readonly FieldInfo? StatusField =
        typeof(SourceCameraCaptureController).GetField("_status", InstancePrivate);
    private static readonly MethodInfo? RecordRenderTimingMethod =
        typeof(SourceCameraCaptureController).GetMethod("RecordRenderTiming", InstancePrivate);
    private static readonly MethodInfo? RecordRenderFrameMethod =
        typeof(SourceCameraCaptureController).GetMethod("RecordRenderFrame", InstancePrivate);

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
    private static readonly MethodInfo? FreeRecordRenderTimingMethod =
        typeof(SceneCameraController).GetMethod("RecordRenderTiming", InstancePrivate);
    private static readonly MethodInfo? FreeRecordRenderFrameMethod =
        typeof(SceneCameraController).GetMethod("RecordRenderFrame", InstancePrivate);

    private static MethodInfo? _screenCaptureMethod;
    private static bool _screenCaptureResolved;
    private static MethodInfo? _urpRenderSingleCameraMethod;
    private static bool _urpRenderSingleCameraResolved;

    private readonly System.Action<ScriptableRenderContext, Camera> _managedEndCameraRenderingHandler;
    private readonly Il2CppSystem.Action<ScriptableRenderContext, Camera> _endCameraRenderingHandler;
    private RenderTexture? _screenCaptureStaging;

    public SourceCaptureEndOfFrameFallback(IntPtr pointer) : base(pointer)
    {
        _managedEndCameraRenderingHandler = OnEndCameraRendering;
        _endCameraRenderingHandler =
            (Il2CppSystem.Action<ScriptableRenderContext, Camera>)_managedEndCameraRenderingHandler;
        RenderPipelineManager.endCameraRendering += _endCameraRenderingHandler;
    }

    public void Start()
    {
        this.StartCoroutine(CaptureLoop());
    }

    public void LateUpdate()
    {
        if (RenderPipelineManager.currentPipeline is null)
        {
            return;
        }

        var directController = SourceCaptureField?.GetValue(null) as SourceCameraCaptureController;
        if (directController?.Enabled == true)
        {
            SetFreeCameraEnabled(false);
            return;
        }

        // When URP exposes its supported standalone camera path, never insert the U3DViewer
        // Camera into the game's normal Camera list. This avoids perturbing the game's own
        // camera ordering/global render state (observed as main-window flicker in Sable).
        if (ResolveUrpRenderSingleCameraMethod() is not null)
        {
            SetFreeCameraEnabled(false);
        }
    }

    private IEnumerator CaptureLoop()
    {
        var wait = new WaitForEndOfFrame();
        while (true)
        {
            yield return wait;

            if (RenderPipelineManager.currentPipeline is null)
            {
                continue;
            }

            var directController = SourceCaptureField?.GetValue(null) as SourceCameraCaptureController;
            if (directController?.Enabled == true)
            {
                SetFreeCameraEnabled(false);
                TryCaptureDirectFallbackFrame(directController);
            }
            else if (ResolveUrpRenderSingleCameraMethod() is null)
            {
                // Non-URP/custom SRP fallback: preserve the previous behaviour. Standard URP
                // is handled in OnEndCameraRendering through RenderSingleCamera instead.
                TryPublishFreeSceneCameraFrameLegacy();
            }
        }
    }

    private void OnEndCameraRendering(ScriptableRenderContext context, Camera completedCamera)
    {
        if (completedCamera is null || RenderPipelineManager.currentPipeline is null)
        {
            return;
        }

        var directController = SourceCaptureField?.GetValue(null) as SourceCameraCaptureController;
        if (directController?.Enabled == true)
        {
            return;
        }

        var renderSingleCamera = ResolveUrpRenderSingleCameraMethod();
        if (renderSingleCamera is null)
        {
            return;
        }

        var controller = SceneCameraField?.GetValue(null) as SceneCameraController;
        if (controller is null || !SceneTransportCoordinator.IsOwner(SceneTransportOwner.FreeCamera))
        {
            return;
        }

        var freeCamera = FreeCameraField?.GetValue(controller) as Camera;
        var renderTexture = FreeRenderTextureField?.GetValue(controller) as RenderTexture;
        var transportTexture = FreeTransportTextureField?.GetValue(controller) as RenderTexture;
        var viewerVisible = FreeViewerVisibleField?.GetValue(controller) is bool visible && visible;
        var bridgeReady = FreeBridgeReadyField?.GetValue(controller) is bool ready && ready;
        var sourceCameraId = FreeEffectiveSourceCameraIdField?.GetValue(controller) is int sourceId
            ? sourceId
            : 0;

        if (freeCamera is null || renderTexture is null || transportTexture is null ||
            !viewerVisible || !bridgeReady || sourceCameraId == 0 ||
            completedCamera.GetInstanceID() != sourceCameraId)
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
            // RenderSingleCamera is URP's supported procedural-camera entry point. Keep this
            // camera disabled so it never participates in the game's normal camera ordering.
            freeCamera.enabled = false;
            freeCamera.targetTexture = renderTexture;
            renderSingleCamera.Invoke(null, new object[] { context, freeCamera });

            // Write the exact NativeBridge source texture immediately before the plugin event,
            // mirroring the direct-capture path that is already known to work in this game.
            Graphics.Blit(renderTexture, transportTexture);
            GL.IssuePluginEvent(renderEvent, copyEventId);

            var elapsedMs =
                (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            FreeRecordRenderTimingMethod?.Invoke(controller, new object[] { elapsedMs });
            FreeRecordRenderFrameMethod?.Invoke(controller, new object[] { now });
            FreeRenderStatusField?.SetValue(
                controller,
                "URP free Scene Camera rendered through UniversalRenderPipeline.RenderSingleCamera; isolated from the game Camera list.");
        }
        catch (TargetInvocationException ex)
        {
            FreeRenderStatusField?.SetValue(
                controller,
                $"URP RenderSingleCamera failed: {ex.InnerException?.Message ?? ex.Message}");
        }
        catch (Exception ex)
        {
            FreeRenderStatusField?.SetValue(controller, $"URP RenderSingleCamera failed: {ex.Message}");
        }
    }

    private void TryCaptureDirectFallbackFrame(SourceCameraCaptureController controller)
    {
        RenderTargetInfo target;
        try
        {
            target = controller.GetRenderTargetInfo();
        }
        catch
        {
            return;
        }

        if (!target.Available)
        {
            return;
        }

        var now = Time.unscaledTime;
        if (NextCaptureAtField?.GetValue(controller) is float nextCaptureAt && now < nextCaptureAt)
        {
            // The normal endCameraRendering callback already produced/scheduled a frame.
            return;
        }

        var renderTexture = RenderTextureField?.GetValue(controller) as RenderTexture;
        var renderEvent = RenderEventField?.GetValue(controller) is IntPtr eventPtr
            ? eventPtr
            : IntPtr.Zero;
        var copyEventId = CopyEventIdField?.GetValue(controller) is int eventId
            ? eventId
            : 0;
        if (renderTexture is null || renderEvent == IntPtr.Zero)
        {
            return;
        }

        var fps = Mathf.Max(MinCaptureFps, target.IdleFps);
        NextCaptureAtField?.SetValue(controller, now + 1f / fps);

        var started = Stopwatch.GetTimestamp();
        try
        {
            var source = FindCamera(target.SourceCameraInstanceId);
            string captureMode;
            if (source?.targetTexture is not null)
            {
                Graphics.Blit(
                    source.targetTexture,
                    renderTexture,
                    new Vector2(1f, -1f),
                    new Vector2(0f, 1f));
                captureMode = $"selected Camera targetTexture ({source.name}), vertically corrected";
            }
            else
            {
                var capture = ResolveScreenCaptureMethod();
                if (capture is null)
                {
                    StatusField?.SetValue(
                        controller,
                        "Direct capture SRP fallback could not resolve UnityEngine.ScreenCapture.CaptureScreenshotIntoRenderTexture.");
                    return;
                }

                var staging = EnsureScreenCaptureStaging(renderTexture.width, renderTexture.height);
                capture.Invoke(null, new object[] { staging });
                Graphics.Blit(
                    staging,
                    renderTexture,
                    new Vector2(1f, -1f),
                    new Vector2(0f, 1f));
                captureMode = "final Game View, vertically corrected";
            }

            GL.IssuePluginEvent(renderEvent, copyEventId);

            var elapsedMs =
                (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            RecordRenderTimingMethod?.Invoke(controller, new object[] { elapsedMs });
            RecordRenderFrameMethod?.Invoke(controller, new object[] { now });

            var pipeline = RenderPipelineManager.currentPipeline;
            var pipelineName = pipeline is null
                ? "Built-in"
                : pipeline.GetType().FullName ?? pipeline.GetType().Name;
            StatusField?.SetValue(
                controller,
                $"Direct capture end-of-frame fallback active via {captureMode}; pipeline {pipelineName}.");
        }
        catch (TargetInvocationException ex)
        {
            StatusField?.SetValue(
                controller,
                $"Direct capture end-of-frame fallback failed: {ex.InnerException?.Message ?? ex.Message}");
        }
        catch (Exception ex)
        {
            StatusField?.SetValue(controller, $"Direct capture end-of-frame fallback failed: {ex.Message}");
        }
    }

    private static void TryPublishFreeSceneCameraFrameLegacy()
    {
        var controller = SceneCameraField?.GetValue(null) as SceneCameraController;
        if (controller is null)
        {
            return;
        }

        var camera = FreeCameraField?.GetValue(controller) as Camera;
        var transportTexture = FreeTransportTextureField?.GetValue(controller) as RenderTexture;
        var viewerVisible = FreeViewerVisibleField?.GetValue(controller) is bool visible && visible;
        var bridgeReady = FreeBridgeReadyField?.GetValue(controller) is bool ready && ready;
        if (camera is null || transportTexture is null || !viewerVisible || !bridgeReady || !camera.enabled || camera.targetTexture is null)
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
            Graphics.Blit(camera.targetTexture, transportTexture);
            GL.IssuePluginEvent(renderEvent, copyEventId);

            var elapsedMs =
                (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            FreeRecordRenderTimingMethod?.Invoke(controller, new object[] { elapsedMs });
            FreeRecordRenderFrameMethod?.Invoke(controller, new object[] { now });

            var pipeline = RenderPipelineManager.currentPipeline;
            var pipelineName = pipeline?.GetType().FullName ?? pipeline?.GetType().Name ?? "unknown SRP";
            FreeRenderStatusField?.SetValue(
                controller,
                $"Custom SRP free Scene Camera frame copied into the NativeBridge transport target; pipeline {pipelineName}.");
        }
        catch (Exception ex)
        {
            FreeRenderStatusField?.SetValue(
                controller,
                $"Custom SRP free Scene Camera publication failed: {ex.Message}");
        }
    }

    private static void SetFreeCameraEnabled(bool enabled)
    {
        var controller = SceneCameraField?.GetValue(null) as SceneCameraController;
        var camera = controller is null ? null : FreeCameraField?.GetValue(controller) as Camera;
        if (camera is not null)
        {
            camera.enabled = enabled;
        }
    }

    private RenderTexture EnsureScreenCaptureStaging(int width, int height)
    {
        if (_screenCaptureStaging is not null &&
            _screenCaptureStaging.width == width &&
            _screenCaptureStaging.height == height)
        {
            return _screenCaptureStaging;
        }

        ReleaseScreenCaptureStaging();
        _screenCaptureStaging = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
        {
            name = "__U3DViewerScreenCaptureStaging"
        };
        _screenCaptureStaging.Create();
        return _screenCaptureStaging;
    }

    private void ReleaseScreenCaptureStaging()
    {
        if (_screenCaptureStaging is null)
        {
            return;
        }

        _screenCaptureStaging.Release();
        UnityEngine.Object.Destroy(_screenCaptureStaging);
        _screenCaptureStaging = null;
    }

    private static Camera? FindCamera(int instanceId)
    {
        if (instanceId == 0)
        {
            return null;
        }

        Camera[] cameras;
        try
        {
            cameras = Camera.allCameras ?? Array.Empty<Camera>();
        }
        catch
        {
            return null;
        }

        for (var index = 0; index < cameras.Length; index++)
        {
            var camera = cameras[index];
            if (camera is not null && camera.GetInstanceID() == instanceId)
            {
                return camera;
            }
        }

        return null;
    }

    private static MethodInfo? ResolveUrpRenderSingleCameraMethod()
    {
        if (_urpRenderSingleCameraResolved)
        {
            return _urpRenderSingleCameraMethod;
        }

        _urpRenderSingleCameraResolved = true;
        Type? urpType = null;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            urpType = assembly.GetType(
                "UnityEngine.Rendering.Universal.UniversalRenderPipeline",
                throwOnError: false);
            if (urpType is not null)
            {
                break;
            }
        }

        if (urpType is null)
        {
            try
            {
                var assembly = Assembly.Load("Unity.RenderPipelines.Universal.Runtime");
                urpType = assembly.GetType(
                    "UnityEngine.Rendering.Universal.UniversalRenderPipeline",
                    throwOnError: false);
            }
            catch
            {
            }
        }

        _urpRenderSingleCameraMethod = urpType?.GetMethod(
            "RenderSingleCamera",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: new[] { typeof(ScriptableRenderContext), typeof(Camera) },
            modifiers: null);
        return _urpRenderSingleCameraMethod;
    }

    private static MethodInfo? ResolveScreenCaptureMethod()
    {
        if (_screenCaptureResolved)
        {
            return _screenCaptureMethod;
        }

        _screenCaptureResolved = true;
        Type? screenCaptureType = null;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!string.Equals(
                    assembly.GetName().Name,
                    "UnityEngine.ScreenCaptureModule",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            screenCaptureType = assembly.GetType("UnityEngine.ScreenCapture", throwOnError: false);
            if (screenCaptureType is not null)
            {
                break;
            }
        }

        if (screenCaptureType is null)
        {
            try
            {
                var assembly = Assembly.Load("UnityEngine.ScreenCaptureModule");
                screenCaptureType = assembly.GetType("UnityEngine.ScreenCapture", throwOnError: false);
            }
            catch
            {
            }
        }

        _screenCaptureMethod = screenCaptureType?.GetMethod(
            "CaptureScreenshotIntoRenderTexture",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: new[] { typeof(RenderTexture) },
            modifiers: null);
        return _screenCaptureMethod;
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

        ReleaseScreenCaptureStaging();
    }
}