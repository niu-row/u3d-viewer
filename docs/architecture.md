# Architecture

## Goal

Observe the live scene of an already-built Unity game from a standalone `U3DViewer.exe`.

The game-side runtime backend can be Mono or IL2CPP, but both produce the same protocol:

```text
Unity game process
  ├─ U3DViewer.Agent.Mono
  │      or
  ├─ U3DViewer.Agent.IL2CPP
  │    ├─ SceneManager / GameObject / Transform / Component
  │    └─ isolated Scene Camera
  │
  ├─ control/data ───── Named Pipe ───────────────┐
  │                                               │
  └─ render target ─── D3D11 shared resource ────┤
                                                  ▼
                                           U3DViewer.exe
                                           ├─ Hierarchy
                                           ├─ Inspector
                                           └─ 3D Scene View
```

## Backend boundary

`U3DViewer.Protocol` does not reference Unity, BepInEx or Il2CppInterop types. Both agents translate runtime objects into plain `SceneSnapshot` DTOs before sending them to the Viewer.

Mono uses `BaseUnityPlugin.Update()` directly. IL2CPP uses `BasePlugin.Load()` and attaches an injected `RuntimeBehaviour` with `AddComponent<T>()` so Unity API access still occurs on Unity's main thread.

The standalone Viewer therefore does not need separate Mono and IL2CPP implementations.

## Control and metadata path

Named Pipe is bidirectional:

```text
Agent -> Viewer
  SceneSnapshot
  RenderTargetInfo

Viewer -> Agent
  camera.move
  camera.look
  camera.speed
  camera.projection
  camera.reset
  camera.focus
```

Unity object APIs are accessed only on Unity's main thread. Each Agent captures DTO snapshots and drains queued Viewer commands from its Unity update callback. Pipe reader/writer threads never directly access Unity objects.

## Scene pixel path

M4 currently targets Windows x64 + Direct3D 11:

```text
Target game
  SceneCamera.Render()
    -> RenderTexture 1280x720 ARGB32
    -> GetNativeTexturePtr()
    -> U3DViewer.NativeBridge
    -> D3D11_RESOURCE_MISC_SHARED_NTHANDLE
    -> IDXGIKeyedMutex

Viewer
    -> OpenSharedResourceByName
    -> keyed mutex acquire
    -> CopyResource to staging texture
    -> Map
    -> Avalonia WriteableBitmap
    -> Scene View
```

The named shared resource avoids transmitting a process-local HANDLE through the control protocol. `SceneSnapshot.RenderTarget` publishes the shared resource name, dimensions, DXGI format and current bridge status.

The staging readback is intentionally a correctness-first implementation. The long-term Viewer should consume the shared GPU texture directly through a suitable graphics/external-image integration path.

## Milestones

### M0 — bootstrap

- BepInEx 6 Mono agent loads in a built Mono game.
- BepInEx 6 IL2CPP agent loads in a built IL2CPP game.
- Standalone Viewer process starts.

### M1 — runtime hierarchy

- Enumerate all loaded scenes.
- Recursively capture root GameObjects and children.
- Capture instance ID, active state, layer, tag, Transform and component type names.
- Stream the same snapshot format from either backend to the standalone Viewer.

### M2 — desktop UI

Implemented:

- Avalonia desktop window.
- Runtime Hierarchy.
- Runtime Inspector.
- Connection/status UI.

### M3 — Scene Camera control

Implemented foundation:

- isolated runtime Camera in both backends.
- bidirectional Viewer commands.
- WASD/QE movement and keyboard look.
- focus selected.
- perspective/orthographic controls.

### M4 — D3D11 Scene transport

Initial implementation present:

- runtime RenderTexture.
- native shared D3D11 Texture2D.
- named NT shared resource.
- keyed-mutex synchronization.
- Viewer-side shared-resource open.
- staging readback into Avalonia Scene View.

Still required before M4 is considered stable:

- real Mono runtime validation.
- real IL2CPP runtime validation.
- resize negotiation.
- device-loss/recreation recovery.
- direct GPU presentation in place of staging readback.

### M5 — scene tools

Planned:

- object picking.
- Renderer bounds.
- Collider visualization.
- Camera frustums.
- grid and transform gizmos.

### M6 — runtime hardening

Planned:

- incremental hierarchy updates instead of full snapshots.
- better IL2CPP runtime component type resolution.
- `DontDestroyOnLoad` and hidden object enumeration.
- compatibility testing across Unity versions.
- DX12/Vulkan transport backends after D3D11 is stable.

## Current constraints

The current runtime path targets Windows x64, Unity Mono/IL2CPP, BepInEx 6 and Direct3D 11. It is read-only and intended for games the operator is authorized to inspect/debug. Runtime validation is local/manual; the repository intentionally does not use GitHub Actions.
