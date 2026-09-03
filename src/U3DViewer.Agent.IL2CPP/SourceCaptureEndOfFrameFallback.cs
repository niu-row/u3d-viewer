using System.Collections;
using System.Diagnostics;
using System.Reflection;
using BepInEx.Unity.IL2CPP.Utils;
using U3DViewer.Protocol;
using UnityEngine;
using UnityEngine.Rendering;

namespace U3DViewer.Agent.IL2CPP;

/// <summary>
/// Some custom SRPs do not raise RenderPipelineManager.endCameraRendering even though
/// the game renders normally. In direct-source mode, fall back to the completed Game View
/// at WaitForEndOfFrame so NativeBridge still receives an actual rendered frame.
/// </summary>
internal sealed class SourceCaptureEndOfFrameFallback : MonoBehaviour
{
    private const float MinCaptureFps = 1f;

    private static readonly BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;

    private static readonly FieldInfo? SourceCaptureField =
        typeof(RuntimeBehaviour).GetField("_sourceCapture", StaticPrivate);
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

    private static MethodInfo? _screenCaptureMethod;
    private static bool _screenCaptureResolved;

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
            TryCaptureFallbackFrame();
        }
    }

    private static void TryCaptureFallbackFrame()
    {
        // Built-in rendering already has the CameraEvent command-buffer path. This fallback
        // is specifically for SRP/custom-SRP games where endCameraRendering is not observed.
        if (RenderPipelineManager.currentPipeline is null)
        {
            return;
        }

        var controller = SourceCaptureField?.GetValue(null) as SourceCameraCaptureController;
        if (controller?.Enabled != true)
        {
            return;
        }

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
                Graphics.Blit(source.targetTexture, renderTexture);
                captureMode = $"selected Camera targetTexture ({source.name})";
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

                capture.Invoke(null, new object[] { renderTexture });
                captureMode = "final Game View";
            }

            // CaptureScreenshotIntoRenderTexture / Graphics.Blit enqueue their GPU work first;
            // enqueue the NativeBridge copy immediately afterwards to preserve render-thread order.
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
}
