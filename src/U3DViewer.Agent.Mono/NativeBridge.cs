using System;
#if LEGACY_MONO
using System.IO;
#endif
using System.Runtime.InteropServices;

namespace U3DViewer.Agent.Mono;

internal static class NativeBridge
{
    private const string LibraryName = "U3DViewer.NativeBridge.dll";

#if LEGACY_MONO
    private static readonly object LoadLock = new object();
    private static IntPtr _loadedModule;
#endif

    internal static int U3DViewer_GetAbiVersion()
    {
        EnsureLoaded();
        return Native_U3DViewer_GetAbiVersion();
    }

    internal static int U3DViewer_SetSourceTexture(IntPtr nativeTexture, string sharedName)
    {
        EnsureLoaded();
        return Native_U3DViewer_SetSourceTexture(nativeTexture, sharedName);
    }

    internal static IntPtr U3DViewer_GetRenderEventFunc()
    {
        EnsureLoaded();
        return Native_U3DViewer_GetRenderEventFunc();
    }

    internal static int U3DViewer_GetCopyEventId()
    {
        EnsureLoaded();
        return Native_U3DViewer_GetCopyEventId();
    }

    internal static int U3DViewer_IsSceneWriterReady(string sharedName)
    {
        EnsureLoaded();
        return Native_U3DViewer_IsSceneWriterReady(sharedName);
    }

    internal static int U3DViewer_GetSourceDxgiFormat()
    {
        EnsureLoaded();
        return Native_U3DViewer_GetSourceDxgiFormat();
    }

    internal static ulong U3DViewer_GetSourceAdapterLuid()
    {
        EnsureLoaded();
        return Native_U3DViewer_GetSourceAdapterLuid();
    }

    internal static int U3DViewer_GetLastError()
    {
        EnsureLoaded();
        return Native_U3DViewer_GetLastError();
    }

    internal static void U3DViewer_Reset()
    {
        EnsureLoaded();
        Native_U3DViewer_Reset();
    }

    private static void EnsureLoaded()
    {
#if LEGACY_MONO
        if (_loadedModule != IntPtr.Zero)
        {
            return;
        }

        lock (LoadLock)
        {
            if (_loadedModule != IntPtr.Zero)
            {
                return;
            }

            var assemblyDirectory = Path.GetDirectoryName(typeof(NativeBridge).Assembly.Location) ?? string.Empty;
            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory ?? string.Empty;
            var candidates = new[]
            {
                Path.Combine(assemblyDirectory, LibraryName),
                Path.Combine(baseDirectory, LibraryName)
            };

            var foundFile = false;
            var lastError = 0;
            var attemptedPath = string.Empty;

            for (var index = 0; index < candidates.Length; index++)
            {
                var candidate = candidates[index];
                if (string.IsNullOrEmpty(candidate) || !File.Exists(candidate))
                {
                    continue;
                }

                foundFile = true;
                attemptedPath = candidate;
                var module = LoadLibrary(candidate);
                if (module != IntPtr.Zero)
                {
                    _loadedModule = module;
                    return;
                }

                lastError = Marshal.GetLastWin32Error();
            }

            if (!foundFile)
            {
                throw new InvalidOperationException(
                    "NativeBridge file was not found beside the Agent or game executable. Checked: " +
                    string.Join("; ", candidates));
            }

            throw new InvalidOperationException(
                "NativeBridge exists but Windows could not load it from '" + attemptedPath +
                "' (Win32 " + lastError + "). Check architecture and native DLL dependencies.");
        }
#endif
    }

    [DllImport(LibraryName, EntryPoint = "U3DViewer_GetAbiVersion", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Native_U3DViewer_GetAbiVersion();

    [DllImport(LibraryName, EntryPoint = "U3DViewer_SetSourceTexture", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int Native_U3DViewer_SetSourceTexture(IntPtr nativeTexture, string sharedName);

    [DllImport(LibraryName, EntryPoint = "U3DViewer_GetRenderEventFunc", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr Native_U3DViewer_GetRenderEventFunc();

    [DllImport(LibraryName, EntryPoint = "U3DViewer_GetCopyEventId", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Native_U3DViewer_GetCopyEventId();

    [DllImport(LibraryName, EntryPoint = "U3DViewer_IsSceneWriterReady", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int Native_U3DViewer_IsSceneWriterReady(string sharedName);

    [DllImport(LibraryName, EntryPoint = "U3DViewer_GetSourceDxgiFormat", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Native_U3DViewer_GetSourceDxgiFormat();

    [DllImport(LibraryName, EntryPoint = "U3DViewer_GetSourceAdapterLuid", CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong Native_U3DViewer_GetSourceAdapterLuid();

    [DllImport(LibraryName, EntryPoint = "U3DViewer_GetLastError", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Native_U3DViewer_GetLastError();

    [DllImport(LibraryName, EntryPoint = "U3DViewer_Reset", CallingConvention = CallingConvention.Cdecl)]
    private static extern void Native_U3DViewer_Reset();

#if LEGACY_MONO
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);
#endif
}
