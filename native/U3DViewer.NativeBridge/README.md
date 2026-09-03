# U3DViewer.NativeBridge

Windows/D3D11 native bridge used by both sides of the standalone Scene View transport.

## Current state

M4 now has an end-to-end implementation path:

```text
Target Unity game
  Scene Camera
    -> RenderTexture (1280x720 ARGB32)
    -> RenderTexture.GetNativeTexturePtr()
    -> U3DViewer_SetSourceTexture(...)
    -> Camera.Render()
    -> GL.IssuePluginEvent(...)
    -> ID3D11DeviceContext::CopyResource
    -> named D3D11 shared Texture2D

Standalone U3DViewer.exe
    -> U3DViewer_OpenSharedTexture(name)
    -> keyed-mutex synchronized CopyResource
    -> CPU-readable staging Texture2D
    -> Avalonia WriteableBitmap
    -> Scene View
```

The first Viewer implementation deliberately performs a GPU-to-CPU staging readback. This is a validation path, not the final performance architecture. Once the transport is proven against real Unity games, the Viewer should move to direct GPU presentation/external-image interop.

## Synchronization

The shared texture uses:

- `D3D11_RESOURCE_MISC_SHARED_NTHANDLE`
- `D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX`
- key `0` for the game-side writer
- key `1` for the Viewer-side reader

The resource is opened by name, so no process-local HANDLE value needs to be sent through the Named Pipe protocol.

## Build

Use a Visual Studio x64 developer shell:

```powershell
cmake -S native/U3DViewer.NativeBridge -B build/native -A x64
cmake --build build/native --config Release
```

Output:

```text
build/native/Release/U3DViewer.NativeBridge.dll
```

## Deployment

For the target Unity game, copy the x64 DLL next to the game executable so the Mono or IL2CPP Agent can resolve the P/Invoke:

```text
<Game>/Game.exe
<Game>/U3DViewer.NativeBridge.dll
```

For the standalone Viewer, copy the same DLL next to the Viewer executable:

```text
<U3DViewer>/U3DViewer.Viewer.exe
<U3DViewer>/U3DViewer.NativeBridge.dll
```

Both processes load the same DLL binary, but each process has independent native global state. The target process uses the writer exports; the Viewer process uses the reader exports.

## Current constraints

- Windows x64 only
- Direct3D 11 only
- 4-byte RGBA/BGRA render target formats only
- fixed 1280x720 Scene Camera target for the first runtime validation
- Viewer currently performs staging readback instead of direct GPU presentation

## Next hardening work

1. Validate the pipeline against a real Mono game and a real IL2CPP game.
2. Add render-target resize negotiation.
3. Add frame statistics and recovery when the target recreates its graphics device.
4. Replace staging readback with direct GPU presentation in the Viewer.
5. Add picking/gizmos after the render path is stable.
