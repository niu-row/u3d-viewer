using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;

namespace U3DViewer.Agent.IL2CPP;

/// <summary>
/// Low-frequency diagnostics for the IL2CPP Scene path. This component is intentionally
/// observational: it does not create/rebind textures, render cameras, or change transport
/// ownership. The goal is to distinguish camera discovery, SRP callback, free-camera render,
/// and NativeBridge publication failures without changing the behaviour being diagnosed.
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
    private static readonly FieldInfo? FreeRenderTextureField =
        typeof(SceneCameraController).GetField("_renderTexture", InstancePrivate);
    private static readonly FieldInfo? FreeTransportTextureField =
        typeof(SceneCameraController).GetField("_transportTexture", InstancePrivate);
    private static readonly FieldInfo? FreeViewerVisibleField =
        typeof(SceneCameraController).GetField("_viewerVisible", InstancePrivate);
    private static readonly FieldInfo? FreeBridgeReadyField =
        typeof(SceneCameraController).GetField("_bridgeReady", InstancePrivate);
    private static readonly FieldInfo? FreeSharedNameField =
        typeof(SceneCameraController).GetField("_sharedName", InstancePrivate);
    private static readonly FieldInfo? FreeRenderGenerationField =
        typeof(SceneCameraController).GetField("_renderGeneration", InstancePrivate);
    private static readonly FieldInfo? FreeSelectedSourceIdField =
        typeof(SceneCameraController).GetField("_selectedSourceCameraInstanceId", InstancePrivate);
    private static readonly FieldInfo? FreeEffectiveSourceIdField =
        typeof(SceneCameraController).GetField("_effectiveSourceCameraInstanceId", InstancePrivate);
    private static readonly FieldInfo? FreeRenderStatusField =
        typeof(SceneCameraController).GetField("_renderStatus", InstancePrivate);

    private static readonly FieldInfo? DirectRenderTextureField =
        typeof(SourceCameraCaptureController).GetField("_renderTexture", InstancePrivate);
    private static readonly FieldInfo? DirectBridgeReadyField =
        typeof(SourceCameraCaptureController).GetField("_bridgeReady", InstancePrivate);
    private static readonly FieldInfo? DirectSharedNameField =
        typeof(SourceCameraCaptureController).GetField("_sharedName", InstancePrivate);
    private static readonly FieldInfo? DirectEffectiveSourceIdField =
        typeof(SourceCameraCaptureController).GetField("_effectiveSourceCameraInstanceId", InstancePrivate);
    private static readonly FieldInfo? DirectStatusField =
        typeof(SourceCameraCaptureController).GetField("_status", InstancePrivate);

    private static readonly FieldInfo? UrpResolvedField =
        typeof(SourceCaptureEndOfFrameFallback).GetField("_urpRenderSingleCameraResolved", StaticPrivate);
    private static readonly FieldInfo? UrpMethodField =
        typeof(SourceCaptureEndOfFrameFallback).GetField("_urpRenderSingleCameraMethod", StaticPrivate);

    private readonly System.Action<ScriptableRenderContext, Camera> _managedEndCameraRenderingHandler;
    private readonly Il2CppSystem.Action<ScriptableRenderContext, Camera> _endCameraRenderingHandler;
    private ManualLogSource? _log;
    private float _nextSummaryAt;
    private int _endCameraCallbacks;
    private int _matchingSourceCallbacks;
    private int _lastCallbackCameraId;
    private string _lastCallbackCameraName = string.Empty;
    private string _lastPipelineState = string.Empty;
    private string _lastCameraInventory = string.Empty;
    private string _lastFreeState = string.Empty;
    private string _lastDirectState = string.Empty;
    private string _lastUrpState = string.Empty;

    public SceneDiagnosticsBehaviour(IntPtr pointer) : base(pointer)
    {
        _managedEndCameraRenderingHandler = OnEndCameraRendering;
        _endCameraRenderingHandler =
            (Il2CppSystem.Action<ScriptableRenderContext, Camera>)_managedEndCameraRenderingHandler;
        RenderPipelineManager.endCameraRendering += _endCameraRenderingHandler;
    }

    public void Start()
    {
        _log = Logger.CreateLogSource("U3DViewer SceneDiag");
        _nextSummaryAt = 0f;
        _log.LogInfo("[SceneDiag] Diagnostics enabled. Logging is observational only; no Scene render/transport state is modified.");
        LogUrpResolution(force: true);
        LogPipeline(force: true);
        LogCameraInventory(force: true);
        LogStateSummary(force: true);
    }

    public void Update()
    {
        var now = Time.unscaledTime;
        if (now < _nextSummaryAt)
        {
            return;
        }

        _nextSummaryAt = now + SummaryIntervalSeconds;
        LogUrpResolution(force: false);
        LogPipeline(force: false);
        LogCameraInventory(force: false);
        LogStateSummary(force: false);
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
            _lastCallbackCameraId = camera.GetInstanceID();
            _lastCallbackCameraName = camera.name ?? string.Empty;

            var controller = SceneCameraField?.GetValue(null) as SceneCameraController;
            var sourceId = controller is null
                ? 0
                : ReadInt(FreeEffectiveSourceIdField, controller);
            if (sourceId != 0 && sourceId == _lastCallbackCameraId)
            {
                _matchingSourceCallbacks++;
            }
        }
        catch
        {
        }
    }

    private void LogUrpResolution(bool force)
    {
        var resolved = UrpResolvedField?.GetValue(null) is bool value && value;
        var method = UrpMethodField?.GetValue(null) as MethodInfo;
        var discoveredType = ResolveUrpType();
        var methods = DescribeRenderSingleCameraMethods(discoveredType);
        var assemblies = DescribeRenderPipelineAssemblies();

        var state =
            $"fallbackResolved={resolved}; selectedMethod={DescribeMethod(method)}; " +
            $"urpType={DescribeType(discoveredType)}; candidates={methods}; assemblies={assemblies}";
        if (!force && string.Equals(state, _lastUrpState, StringComparison.Ordinal))
        {
            return;
        }

        _lastUrpState = state;
        _log?.LogInfo($"[SceneDiag] URP resolution: {state}");
    }

    private void LogPipeline(bool force)
    {
        string currentPipeline;
        try
        {
            var pipeline = RenderPipelineManager.currentPipeline;
            currentPipeline = pipeline is null
                ? "<null/Built-in>"
                : DescribeRuntimeObject(pipeline);
        }
        catch (Exception ex)
        {
            currentPipeline = $"<error:{ex.GetType().Name}:{ex.Message}>";
        }

        var asset = DescribeCurrentRenderPipelineAsset();
        var state = $"currentPipeline={currentPipeline}; asset={asset}";
        if (!force && string.Equals(state, _lastPipelineState, StringComparison.Ordinal))
        {
            return;
        }

        _lastPipelineState = state;
        _log?.LogInfo($"[SceneDiag] Pipeline: {state}");
    }

    private void LogCameraInventory(bool force)
    {
        var inventory = BuildCameraInventory();
        if (!force && string.Equals(inventory, _lastCameraInventory, StringComparison.Ordinal))
        {
            return;
        }

        _lastCameraInventory = inventory;
        _log?.LogInfo($"[SceneDiag] Cameras: {inventory}");
    }

    private void LogStateSummary(bool force)
    {
        var sceneCamera = SceneCameraField?.GetValue(null) as SceneCameraController;
        var direct = SourceCaptureField?.GetValue(null) as SourceCameraCaptureController;

        var freeState = BuildFreeState(sceneCamera);
        var directState = BuildDirectState(direct);
        var ownerState = $"owner={SceneTransportCoordinator.Owner}; epoch={SceneTransportCoordinator.Epoch}";

        if (force || !string.Equals(freeState, _lastFreeState, StringComparison.Ordinal))
        {
            _lastFreeState = freeState;
            _log?.LogInfo($"[SceneDiag] FreeCamera: {freeState}");
        }

        if (force || !string.Equals(directState, _lastDirectState, StringComparison.Ordinal))
        {
            _lastDirectState = directState;
            _log?.LogInfo($"[SceneDiag] DirectCapture: {directState}");
        }

        _log?.LogInfo(
            $"[SceneDiag] Summary: {ownerState}; endCameraRendering callbacks/{SummaryIntervalSeconds:0.0}s=" +
            $"{_endCameraCallbacks}, selected-source matches={_matchingSourceCallbacks}, " +
            $"last={_lastCallbackCameraName}#{_lastCallbackCameraId}; NativeBridge HRESULT={ReadNativeError()}");

        _endCameraCallbacks = 0;
        _matchingSourceCallbacks = 0;
    }

    private static string BuildFreeState(SceneCameraController? controller)
    {
        if (controller is null)
        {
            return "controller=<null>";
        }

        var camera = FreeCameraField?.GetValue(controller) as Camera;
        var renderTexture = FreeRenderTextureField?.GetValue(controller) as RenderTexture;
        var transport = FreeTransportTextureField?.GetValue(controller) as RenderTexture;
        var viewerVisible = ReadBool(FreeViewerVisibleField, controller);
        var bridgeReady = ReadBool(FreeBridgeReadyField, controller);
        var sharedName = ReadString(FreeSharedNameField, controller);
        var selectedId = ReadInt(FreeSelectedSourceIdField, controller);
        var sourceId = ReadInt(FreeEffectiveSourceIdField, controller);
        var generation = ReadInt(FreeRenderGenerationField, controller);
        var status = ReadString(FreeRenderStatusField, controller);
        var writer = ReadWriterReady(sharedName);

        return
            $"viewerVisible={viewerVisible}; bridgeReady={bridgeReady}; selectedSource={selectedId}; effectiveSource={sourceId}; " +
            $"generation={generation}; shared={sharedName}; writerReady={writer}; " +
            $"camera={DescribeCamera(camera)}; renderRT={DescribeRenderTexture(renderTexture)}; " +
            $"transportRT={DescribeRenderTexture(transport)}; status={status}";
    }

    private static string BuildDirectState(SourceCameraCaptureController? controller)
    {
        if (controller is null)
        {
            return "controller=<null>";
        }

        var enabled = controller.Enabled;
        var renderTexture = DirectRenderTextureField?.GetValue(controller) as RenderTexture;
        var bridgeReady = ReadBool(DirectBridgeReadyField, controller);
        var sharedName = ReadString(DirectSharedNameField, controller);
        var sourceId = ReadInt(DirectEffectiveSourceIdField, controller);
        var status = ReadString(DirectStatusField, controller);
        var writer = ReadWriterReady(sharedName);

        return
            $"enabled={enabled}; bridgeReady={bridgeReady}; effectiveSource={sourceId}; shared={sharedName}; " +
            $"writerReady={writer}; renderRT={DescribeRenderTexture(renderTexture)}; status={status}";
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
            var target = camera.targetTexture is null
                ? "screen"
                : DescribeRenderTexture(camera.targetTexture);
            return
                $"{camera.name}#{camera.GetInstanceID()} enabled={camera.enabled} active={camera.gameObject.activeInHierarchy} " +
                $"depth={camera.depth:0.###} target={target} mask=0x{camera.cullingMask:X8} " +
                $"ortho={camera.orthographic} fov={camera.fieldOfView:0.###} pos={FormatVector(camera.transform.position)}";
        }
        catch (Exception ex)
        {
            return $"<camera wrapper error:{ex.GetType().Name}:{ex.Message}>";
        }
    }

    private static string DescribeRenderTexture(RenderTexture? texture)
    {
        if (texture is null)
        {
            return "<null>";
        }

        try
        {
            return
                $"{texture.name}[{texture.width}x{texture.height},fmt={texture.format},created={texture.IsCreated()}]";
        }
        catch (Exception ex)
        {
            return $"<RT wrapper error:{ex.GetType().Name}:{ex.Message}>";
        }
    }

    private static string ReadWriterReady(string sharedName)
    {
        if (string.IsNullOrWhiteSpace(sharedName))
        {
            return "n/a";
        }

        try
        {
            return NativeBridge.U3DViewer_IsSceneWriterReady(sharedName).ToString();
        }
        catch (Exception ex)
        {
            return $"error:{ex.GetType().Name}";
        }
    }

    private static string ReadNativeError()
    {
        try
        {
            return $"0x{NativeBridge.U3DViewer_GetLastError():X8}";
        }
        catch (Exception ex)
        {
            return $"<error:{ex.GetType().Name}>";
        }
    }

    private static Type? ResolveUrpType()
    {
        try
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(
                    "UnityEngine.Rendering.Universal.UniversalRenderPipeline",
                    throwOnError: false);
                if (type is not null)
                {
                    return type;
                }
            }
        }
        catch
        {
        }
        return null;
    }

    private static string DescribeRenderSingleCameraMethods(Type? urpType)
    {
        if (urpType is null)
        {
            return "<none>";
        }

        try
        {
            var methods = urpType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            var builder = new StringBuilder();
            foreach (var method in methods)
            {
                if (!string.Equals(method.Name, "RenderSingleCamera", StringComparison.Ordinal))
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append(" | ");
                }
                builder.Append(DescribeMethod(method));
            }
            return builder.Length == 0 ? "<none>" : builder.ToString();
        }
        catch (Exception ex)
        {
            return $"<error:{ex.GetType().Name}:{ex.Message}>";
        }
    }

    private static string DescribeRenderPipelineAssemblies()
    {
        try
        {
            var names = new List<string>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = assembly.GetName().Name ?? string.Empty;
                if (name.IndexOf("RenderPipeline", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Universal", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    names.Add(name);
                }
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names.Count == 0 ? "<none>" : string.Join(",", names.ToArray());
        }
        catch (Exception ex)
        {
            return $"<error:{ex.GetType().Name}>";
        }
    }

    private static string DescribeCurrentRenderPipelineAsset()
    {
        try
        {
            var property = typeof(GraphicsSettings).GetProperty(
                "currentRenderPipeline",
                BindingFlags.Public | BindingFlags.Static);
            var asset = property?.GetValue(null);
            return asset is null ? "<null>" : DescribeRuntimeObject(asset);
        }
        catch (Exception ex)
        {
            return $"<error:{ex.GetType().Name}:{ex.Message}>";
        }
    }

    private static string DescribeRuntimeObject(object value)
    {
        try
        {
            var type = value.GetType();
            return $"{type.FullName ?? type.Name} [asm={type.Assembly.GetName().Name}]";
        }
        catch
        {
            return value.ToString() ?? "<unknown>";
        }
    }

    private static string DescribeType(Type? type) =>
        type is null
            ? "<not found>"
            : $"{type.FullName ?? type.Name} [asm={type.Assembly.GetName().Name}]";

    private static string DescribeMethod(MethodInfo? method)
    {
        if (method is null)
        {
            return "<null>";
        }

        try
        {
            var parameters = method.GetParameters();
            var args = string.Join(",", parameters.Select(parameter => parameter.ParameterType.FullName ?? parameter.ParameterType.Name).ToArray());
            return $"{method.DeclaringType?.FullName}.{method.Name}({args}) public={method.IsPublic}";
        }
        catch
        {
            return method.Name;
        }
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

    private static string FormatVector(Vector3 value) =>
        $"({value.x:0.##},{value.y:0.##},{value.z:0.##})";

    public void OnDestroy()
    {
        try
        {
            RenderPipelineManager.endCameraRendering -= _endCameraRenderingHandler;
        }
        catch
        {
        }

        _log = null;
    }
}
