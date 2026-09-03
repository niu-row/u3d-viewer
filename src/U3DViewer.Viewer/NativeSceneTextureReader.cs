using System.Runtime.InteropServices;
using U3DViewer.Protocol;

namespace U3DViewer.Viewer;

internal sealed class NativeSceneTextureReader : IDisposable
{
    private const string LibraryName = "U3DViewer.NativeBridge";
    private string _sharedName = string.Empty;
    private bool _opened;

    public string LastStatus { get; private set; } = "No shared Scene texture selected.";

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

        if (_opened && string.Equals(_sharedName, target.SharedName, StringComparison.Ordinal))
        {
            return true;
        }

        Reset();

        try
        {
            if (U3DViewer_OpenSharedTexture(target.SharedName) == 0)
            {
                LastStatus = $"Could not open shared Scene texture '{target.SharedName}' (HRESULT 0x{U3DViewer_GetLastError():X8}).";
                return false;
            }

            _sharedName = target.SharedName;
            _opened = true;
            LastStatus = $"Opened shared Scene texture {target.Width}×{target.Height}.";
            return true;
        }
        catch (DllNotFoundException)
        {
            LastStatus = "U3DViewer.NativeBridge.dll was not found next to U3DViewer.Viewer.exe.";
            return false;
        }
        catch (EntryPointNotFoundException ex)
        {
            LastStatus = $"NativeBridge API mismatch: {ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            LastStatus = $"Opening shared Scene texture failed: {ex.Message}";
            return false;
        }
    }

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
        _opened = false;
    }

    public void Dispose() => Reset();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int U3DViewer_OpenSharedTexture(string sharedName);

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
