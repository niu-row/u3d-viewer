using System.Collections;
using System.Diagnostics;
using System.Reflection;
using BepInEx.Unity.IL2CPP.Utils;
using U3DViewer.Protocol;
using UnityEngine;
using UnityEngine.Rendering;

namespace U3DViewer.Agent.IL2CPP;

/// <summary>
/// End-of-frame transport support for SRP games. Direct-source mode falls back to the
/// completed Game View when camera callbacks are unavailable; free-camera mode publishes
/// the RenderTexture that the active RenderPipeline rendered for the U3DViewer Camera.
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
    private static readonly FieldInfo? FreeViewerVisibleField =
        typeof(SceneCameraController).GetField("_viewerVisible", InstancePrivate);
    private static readonly FieldInfo? FreeBridgeReadyField =
        typeof(SceneCameraController).GetField("_bridgeReady", InstancePrivate);
    private static readonly FieldInfo? FreeRenderEventField =
        typeof(SceneCameraController).GetField("_renderEvent", InstancePrivate);
    private static readonly FieldInfo? FreeCopyEventIdField =
        typeof(SceneCameraController).GetField("_copyEventId", InstancePrivate);
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

    private RenderTexture? _screenCaptureStaging;

    public SourceCaptureEndOfFrameFallback(IntPtr pointer) : base(pointer)
    {
    }

    public void Start()
    {
        this.StartCoroutine(CaptureLoop());
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
            else
            {
                TryPublishFreeSceneCameraFrame();
            }
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

            // Screen capture / Blit enqueue their GPU work first; enqueue the NativeBridge
            // copy immediately afterwards to preserve render-thread order.
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

    private static void TryPublishFreeSceneCameraFrame()
    {
        var controller = SceneCameraField?.GetValue(null) as SceneCameraController;
        if (controller is null)
        {
            return;
        }

        var camera = FreeCameraField?.GetValue(controller) as Camera;
        var viewerVisible = FreeViewerVisibleField?.GetValue(controller) is bool visible && visible;
        var bridgeReady = FreeBridgeReadyField?.GetValue(controller) is bool ready && ready;
        if (camera is null || !viewerVisible || !bridgeReady || !camera.enabled || camera.targetTexture is null)
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
            // The active SRP has already rendered the enabled U3DViewer Camera into its
            // targetTexture during this frame. Publish that completed RenderTexture now.
            GL.IssuePluginEvent(renderEvent, copyEventId);

            var elapsedMs =
                (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            FreeRecordRenderTimingMethod?.Invoke(controller, new object[] { elapsedMs });
            FreeRecordRenderFrameMethod?.Invoke(controller, new object[] { now });

            var pipeline = RenderPipelineManager.currentPipeline;
            var pipelineName = pipeline?.GetType().FullName ?? pipeline?.GetType().Name ?? "unknown SRP";
            FreeRenderStatusField?.SetValue(
                controller,
                $"SRP free Scene Camera is rendered by {pipelineName}; shared frame published at end-of-frame.");
        }
        catch (Exception ex)
        {
            FreeRenderStatusField?.SetValue(
                controller,
                $"SRP free Scene Camera publication failed: {ex.Message}");
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

        var cameras = Camera.allCameras;
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
        ReleaseScreenCaptureStaging();
    }
}
