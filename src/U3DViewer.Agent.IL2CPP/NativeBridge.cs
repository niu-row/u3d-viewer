using System.Runtime.InteropServices;

namespace U3DViewer.Agent.IL2CPP;

internal static class NativeBridge
{
    private const string LibraryName = "U3DViewer.NativeBridge";
    private const string DirectCapturePrefix = "U3DViewer.Scene.Source.";

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int U3DViewer_GetAbiVersion();

    [DllImport(LibraryName, EntryPoint = "U3DViewer_SetSourceTexture", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int SetSourceTextureNative(IntPtr nativeTexture, string sharedName);

    internal static int U3DViewer_SetSourceTexture(IntPtr nativeTexture, string sharedName)
    {
        var owner = sharedName.StartsWith(DirectCapturePrefix, StringComparison.Ordinal)
            ? SceneTransportOwner.DirectCapture
            : SceneTransportOwner.FreeCamera;
        SceneTransportCoordinator.Observe(owner);
        return SetSourceTextureNative(nativeTexture, sharedName);
    }

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr U3DViewer_GetRenderEventFunc();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int U3DViewer_GetCopyEventId();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int U3DViewer_IsSceneWriterReady(string sharedName);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int U3DViewer_GetSourceDxgiFormat();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ulong U3DViewer_GetSourceAdapterLuid();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int U3DViewer_GetLastError();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void U3DViewer_Reset();
}
