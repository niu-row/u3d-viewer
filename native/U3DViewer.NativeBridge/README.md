# U3DViewer.NativeBridge

Windows/D3D11 native bridge for the future standalone Scene View pixel transport.

## Current state

This directory contains the low-level shared-texture primitive only. It is not wired into the Mono/IL2CPP agents yet and the Viewer does not open the resource yet.

The intended render path is:

```text
Unity Scene Camera
  -> RenderTexture
  -> RenderTexture.GetNativeTexturePtr()
  -> U3DViewer_SetSourceTexture(...)
  -> GL.IssuePluginEvent(U3DViewer_GetRenderEventFunc(), copyEventId)
  -> ID3D11DeviceContext::CopyResource
  -> named D3D11 shared Texture2D
  -> Viewer opens resource by name
```

The shared texture uses:

- `D3D11_RESOURCE_MISC_SHARED_NTHANDLE`
- `D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX`
- key `0` for the game-side writer
- key `1` for the Viewer-side reader

Using a named NT shared resource avoids passing a process-local HANDLE value through the control protocol.

## Build

Use a Visual Studio x64 developer shell:

```powershell
cmake -S native/U3DViewer.NativeBridge -B build/native -A x64
cmake --build build/native --config Release
```

Output: `U3DViewer.NativeBridge.dll`.

## Next integration step

1. Create the Scene Camera `RenderTexture` in each Agent.
2. Load/PInvoke this DLL from the Agent.
3. Register the RenderTexture pointer and a unique resource name.
4. Issue the copy plugin event after the Scene Camera renders.
5. Announce the shared resource name/size/format to the Viewer.
6. Open it in the Viewer with D3D11 and release keyed mutex key `0` after sampling.
