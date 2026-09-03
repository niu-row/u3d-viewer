using System.Diagnostics;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using U3DViewer.Protocol;

namespace U3DViewer.Agent.IL2CPP;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "dev.u3dviewer.agent.il2cpp";
    public const string PluginName = "U3D Viewer Agent (IL2CPP)";
    public const string PluginVersion = "0.1.0";

    private static ManualLogSource? _sharedLog;
    private static bool _bootstrapResizeNoticeLogged;
    private static bool _bootstrapRecoverNoticeLogged;

    private PipeServer? _pipeServer;
    private Harmony? _sceneTransportHarmony;

    public override void Load()
    {
        _sharedLog = Log;
        _bootstrapResizeNoticeLogged = false;
        _bootstrapRecoverNoticeLogged = false;
        InstallSceneTransportCompatibility();

        var pipeName = $"u3d-viewer-{Process.GetCurrentProcess().Id}";
        _pipeServer = new PipeServer(pipeName, Log);
        _pipeServer.Start();

        RuntimeBehaviour.Initialize(_pipeServer, Log);
        AddComponent<RuntimeBehaviour>();
        AddComponent<SourceCaptureEndOfFrameFallback>();

        Log.LogInfo($"U3D Viewer IL2CPP agent loaded. Pipe: {pipeName}");
    }

    public override bool Unload()
    {
        var harmony = _sceneTransportHarmony;
        _sceneTransportHarmony = null;
        if (harmony is not null)
        {
            try
            {
                harmony.UnpatchSelf();
            }
            catch (Exception ex)
            {
                Log.LogWarning($"Could not remove IL2CPP Scene transport compatibility patches: {ex.Message}");
            }
        }

        RuntimeBehaviour.Shutdown();
        _pipeServer?.Dispose();
        _pipeServer = null;
        _sharedLog = null;
        return true;
    }

    private void InstallSceneTransportCompatibility()
    {
        var harmony = new Harmony(PluginGuid + ".scene-transport");
        try
        {
            harmony.CreateClassProcessor(typeof(SceneCommandBootstrapPatch)).Patch();
            harmony.CreateClassProcessor(typeof(SceneWriterStatusPatch)).Patch();
            _sceneTransportHarmony = harmony;
            Log.LogInfo("Installed IL2CPP Scene first-frame transport guard.");
        }
        catch (Exception ex)
        {
            try
            {
                harmony.UnpatchSelf();
            }
            catch
            {
            }

            _sceneTransportHarmony = null;
            Log.LogWarning($"Could not install IL2CPP Scene first-frame transport guard: {ex.Message}");
        }
    }

    [HarmonyPatch(typeof(SceneCameraController), nameof(SceneCameraController.Apply))]
    private static class SceneCommandBootstrapPatch
    {
        [HarmonyPrefix]
        private static bool StabilizeFirstSharedFrame(SceneCameraController __instance, ref ViewerCommand command)
        {
            if (command.Kind != ViewerCommandKind.CameraStreamSettings &&
                command.Kind != ViewerCommandKind.CameraRecover)
            {
                return true;
            }

            RenderTargetInfo target;
            try
            {
                target = __instance.GetRenderTargetInfo();
            }
            catch
            {
                return true;
            }

            if (!target.Available || string.IsNullOrWhiteSpace(target.SharedName))
            {
                return true;
            }

            try
            {
                if (NativeBridge.U3DViewer_IsSceneWriterReady(target.SharedName) != 0)
                {
                    return true;
                }
            }
            catch
            {
                return true;
            }

            var hresult = 0;
            try
            {
                hresult = NativeBridge.U3DViewer_GetLastError();
            }
            catch
            {
            }

            if (command.Kind == ViewerCommandKind.CameraRecover)
            {
                if (!_bootstrapRecoverNoticeLogged)
                {
                    _bootstrapRecoverNoticeLogged = true;
                    _sharedLog?.LogInfo(
                        $"Deferred Scene transport recovery until NativeBridge publishes the first shared frame (HRESULT 0x{hresult:X8}).");
                }
                return false;
            }

            var requestedWidth = (int)command.Z;
            var requestedHeight = (int)command.Value;
            if (requestedWidth != target.Width || requestedHeight != target.Height)
            {
                command = new ViewerCommand(
                    ViewerCommandKind.CameraStreamSettings,
                    command.X,
                    command.Y,
                    target.Width,
                    target.Height);

                if (!_bootstrapResizeNoticeLogged)
                {
                    _bootstrapResizeNoticeLogged = true;
                    _sharedLog?.LogInfo(
                        $"Deferred Scene source resize {requestedWidth}x{requestedHeight} until NativeBridge publishes the first shared frame; keeping generation at {target.Width}x{target.Height} (HRESULT 0x{hresult:X8}).");
                }
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(SceneCameraController), nameof(SceneCameraController.GetRenderTargetInfo))]
    private static class SceneWriterStatusPatch
    {
        [HarmonyPostfix]
        private static void AppendWriterState(RenderTargetInfo __result)
        {
            if (!__result.Available || string.IsNullOrWhiteSpace(__result.SharedName))
            {
                return;
            }

            try
            {
                if (NativeBridge.U3DViewer_IsSceneWriterReady(__result.SharedName) != 0)
                {
                    return;
                }

                var hresult = NativeBridge.U3DViewer_GetLastError();
                __result.Status +=
                    $" NativeBridge first shared frame pending (HRESULT 0x{hresult:X8}); source resize/recovery is deferred.";
            }
            catch
            {
            }
        }
    }
}
