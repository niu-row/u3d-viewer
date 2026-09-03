using System.Runtime.InteropServices;
using System.Text;
using U3DViewer.Protocol;

namespace U3DViewer.Viewer;

internal sealed class NativeSceneTextureReader : IDisposable
{
    private const string LibraryName = "U3DViewer.NativeBridge";
    private string _sharedName = string.Empty;
    private ulong _targetAdapterLuid;
    private bool _opened;

    public string LastStatus { get; private set; } = "No shared Scene texture selected.";
    public ulong ReaderAdapterLuid { get; private set; }
    public string ReaderAdapterName { get; private set; } = string.Empty;

    public bool Open(RenderTargetInfo target)
    {
        if (!OperatingSystem.IsWindows())
        {
            LastStatus = "Scene texture transport currently requires Windows/D3D11.";
            return false;
        }

        if (!target.Available || string.IsNullOrWhiteSpace(target.SharedName))
        {
            LastStatus = string.IsNullOrWhiteSpace(target.Status)
                ? "Agent has not published a shared Scene texture."
                : target.Status;
            return false;
        }

        if (_opened &&
            string.Equals(_sharedName, target.SharedName, StringComparison.Ordinal) &&
            _targetAdapterLuid == target.AdapterLuid)
        {
            return true;
        }

        Reset();

        try
        {
            ViewerLog.Info($"Scene writer GPU: {FormatAdapter(target.AdapterName, target.AdapterLuid)}");

            var opened = target.AdapterLuid != 0
                ? U3DViewer_OpenSharedTextureOnAdapter(target.SharedName, target.AdapterLuid)
                : U3DViewer_OpenSharedTexture(target.SharedName);

            UpdateReaderAdapterInfo();
            var readerDescription = FormatAdapter(ReaderAdapterName, ReaderAdapterLuid);
            ViewerLog.Info($"Scene reader GPU: {readerDescription}");

            if (opened == 0)
            {
                var hresult = U3DViewer_GetLastError();
                LastStatus =
                    $"Could not open shared Scene texture '{target.SharedName}' (HRESULT 0x{hresult:X8}). " +
                    $"Game GPU: {FormatAdapter(target.AdapterName, target.AdapterLuid)} · " +
                    $"Viewer GPU: {readerDescription}";
                ViewerLog.Error(LastStatus);
                return false;
            }

            if (target.AdapterLuid != 0 &&
                ReaderAdapterLuid != 0 &&
                ReaderAdapterLuid != target.AdapterLuid)
            {
                ViewerLog.Warning(
                    $"Scene GPU LUID mismatch after open. Game=0x{target.AdapterLuid:X16}, Viewer=0x{ReaderAdapterLuid:X16}.");
            }

            _sharedName = target.SharedName;
            _targetAdapterLuid = target.AdapterLuid;
            _opened = true;
            LastStatus =
                $"Opened shared Scene texture {target.Width}×{target.Height}. " +
                $"Game GPU: {FormatAdapter(target.AdapterName, target.AdapterLuid)} · " +
                $"Viewer GPU: {readerDescription}";
            ViewerLog.Info(LastStatus);
            return true;
        }
        catch (DllNotFoundException)
        {
            LastStatus = "U3DViewer.NativeBridge.dll was not found next to U3DViewer.Viewer.exe.";
            ViewerLog.Error(LastStatus);
            return false;
        }
        catch (EntryPointNotFoundException ex)
        {
            LastStatus = $"NativeBridge API mismatch: {ex.Message}. Rebuild U3DViewer so the Viewer and game receive the same NativeBridge DLL.";
            ViewerLog.Error(LastStatus, ex);
            return false;
        }
        catch (Exception ex)
        {
            LastStatus = $"Opening shared Scene texture failed: {ex.Message}";
            ViewerLog.Error(LastStatus, ex);
            return false;
        }
    }

    public string DescribeGpuPair(RenderTargetInfo target) =>
        $"Game GPU: {FormatAdapter(target.AdapterName, target.AdapterLuid)} · " +
        $"Viewer GPU: {FormatAdapter(ReaderAdapterName, ReaderAdapterLuid)}";

    public bool TryRead(IntPtr destination, int destinationStride, int destinationHeight, out int width, out int height, out int dxgiFormat)
    {
        width = 0;
        height = 0;
        dxgiFormat = 0;

        if (!_opened)
        {
            return false;
        }

        try
        {
            return U3DViewer_ReadSharedTexture(
                destination,
                destinationStride,
                destinationHeight,
                out width,
                out height,
                out dxgiFormat) != 0;
        }
        catch (Exception ex)
        {
            LastStatus = $"Reading shared Scene texture failed: {ex.Message}";
            ViewerLog.Error(LastStatus, ex);
            _opened = false;
            return false;
        }
    }

    public void Reset()
    {
        if (_opened)
        {
            try
            {
                U3DViewer_ResetReader();
            }
            catch
            {
                // The bridge may disappear while the target process/viewer is shutting down.
            }
        }

        _sharedName = string.Empty;
        _targetAdapterLuid = 0;
        _opened = false;
        ReaderAdapterLuid = 0;
        ReaderAdapterName = string.Empty;
    }

    public void Dispose() => Reset();

    private void UpdateReaderAdapterInfo()
    {
        ReaderAdapterLuid = U3DViewer_GetReaderAdapterLuid();

        var name = new StringBuilder(256);
        ReaderAdapterName = U3DViewer_GetReaderAdapterName(name, name.Capacity) != 0
            ? name.ToString()
            : string.Empty;
    }

    private static string FormatAdapter(string? name, ulong luid)
    {
        var displayName = string.IsNullOrWhiteSpace(name) ? "unknown GPU" : name.Trim();
        return luid == 0
            ? displayName
            : $"{displayName} [LUID 0x{luid:X16}]";
    }

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int U3DViewer_OpenSharedTexture(string sharedName);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int U3DViewer_OpenSharedTextureOnAdapter(string sharedName, ulong adapterLuid);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong U3DViewer_GetReaderAdapterLuid();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int U3DViewer_GetReaderAdapterName(StringBuilder buffer, int capacity);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int U3DViewer_ReadSharedTexture(
        IntPtr destination,
        int destinationStride,
        int destinationHeight,
        out int width,
        out int height,
        out int dxgiFormat);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int U3DViewer_GetLastError();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void U3DViewer_ResetReader();
}
