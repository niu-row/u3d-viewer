# U3DViewer.NativeBridge

Windows/D3D11 native bridge used by the standalone Scene View transport.

## Current path

The target Unity process owns the writer side:

```text
Scene Camera
  -> RenderTexture (configurable size, ARGB32)
  -> RenderTexture.GetNativeTexturePtr()
  -> U3DViewer_SetSourceTexture(...)
  -> Camera.Render()
  -> GL.IssuePluginEvent(...)
  -> ID3D11DeviceContext::CopyResource
  -> named D3D11 shared Texture2D
  -> IDXGIKeyedMutex (writer key 0 -> 1)
```

The standalone Viewer owns the presenter side:

```text
Avalonia NativeControlHost
  -> Win32 child HWND
  -> D3D11 device on the same DXGI adapter LUID
  -> OpenSharedResourceByName
  -> IDXGIKeyedMutex (reader key 1 -> 0)
  -> ShaderResourceView
  -> fullscreen triangle shader
  -> GPU-side RenderTexture Y flip
  -> aspect-preserving viewport / letterbox
  -> DXGI swap chain
  -> Present
```

There is no active GPU-to-CPU staging readback or `WriteableBitmap` Scene path. The legacy CPU reader API has been removed; `NativeBridge.cpp` now contains only the Unity-side shared-texture writer, while `ScenePresenter.cpp` contains the Viewer-side GPU presenter and native Scene input host.

## Shared resource

The shared texture uses:

- `D3D11_RESOURCE_MISC_SHARED_NTHANDLE`
- `D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX`
- key `0` for the game-side writer
- key `1` for the Viewer-side presenter

The resource is opened by name, so no process-local HANDLE value is sent over the control protocol. The writer keeps the named NT shared handle alive for the lifetime of the resource.

Changing Scene resolution recreates the Unity RenderTexture and shared resource with a new generation name so the Viewer does not accidentally reuse an old D3D11 object.

## Native responsibilities

`src/NativeBridge.cpp`:

- accepts the Unity RenderTexture native pointer;
- creates the named shared D3D11 texture;
- copies the rendered frame on Unity's render thread;
- publishes source DXGI format and adapter LUID;
- owns writer-side keyed-mutex synchronization.

`src/ScenePresenter.cpp`:

- creates the embedded Win32 Scene HWND;
- selects the Viewer D3D11 device by source adapter LUID;
- opens the named shared resource;
- creates the SRV/shaders/swap chain;
- presents without CPU readback;
- receives Raw Input for Scene fly-camera controls;
- reports presenter initialization stages and HRESULT diagnostics.

## Build

Use a Visual Studio x64 developer environment, or run the repository build script:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1
```

Direct CMake build:

```powershell
cmake -S native/U3DViewer.NativeBridge -B build/native -A x64
cmake --build build/native --config Release
```

Output:

```text
build/native/Release/U3DViewer.NativeBridge.dll
```

The normal Viewer build copies this DLL next to `U3DViewer.Viewer.exe`. Runtime preparation also deploys the same DLL next to the target game executable for Agent P/Invoke.

## Current constraints

- Windows x64
- Direct3D 11
- single-sample shared Scene texture
- 4-byte RGBA/BGRA-style Scene formats currently expected by the presentation path
- Viewer and game must be able to open the shared resource on the same DXGI adapter

## Hardening still worth testing

- graphics-device loss/recreation
- keyed-mutex abandonment/recovery
- broader hybrid-GPU/driver coverage
- additional texture-format cases
- future DX12/Vulkan transport backends after D3D11 is fully hardened
