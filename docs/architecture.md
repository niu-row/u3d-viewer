# Architecture

## Goal

Observe the live scene of an already-built Unity game from a standalone `U3DViewer.exe`, with target setup driven from the GUI instead of per-game configuration files.

```text
U3DViewer.exe
├─ Process Picker / Open Game
├─ Runtime Preparation
│  ├─ detect Mono / IL2CPP
│  ├─ ensure BepInEx 6 x64
│  ├─ generate IL2CPP interop when required
│  ├─ fingerprint target compatibility
│  ├─ reuse cached Agent OR build/cache once
│  └─ deploy / launch / wait for Agent
├─ Hierarchy
├─ Inspector
└─ Scene View
          │
          ▼
Unity game process
├─ U3DViewer.Agent.Mono OR U3DViewer.Agent.IL2CPP
├─ Named Pipe: u3d-viewer-<PID>
└─ D3D11 named shared Texture2D
```

## Runtime preparation boundary

The normal development build does not know a target game. `Ctrl+Shift+B` builds Viewer + NativeBridge and copies the Agent/Protocol source projects into `agent-builder/` beside the Viewer.

After the user selects a game, the Viewer:

1. detects the Unity backend from the game layout;
2. installs the pinned BepInEx 6 x64 runtime when BepInEx is absent;
3. for IL2CPP, launches a temporary bootstrap process when `BepInEx/interop` still needs to be generated;
4. computes a compatibility fingerprint from the selected backend, Agent/Protocol builder inputs, and SHA-256 hashes of the Unity assemblies used at compile time;
5. reuses `%LOCALAPPDATA%/U3DViewer/AgentCache/<backend>/<fingerprint>/` when that profile already exists;
6. otherwise copies the bundled Agent Builder workspace to `%LOCALAPPDATA%/U3DViewer/AgentBuilder/`, invokes the local .NET SDK with `UnityReferenceDir`, and stores the resulting Agent + Protocol in the cache;
7. deploys Agent + Protocol + NativeBridge;
8. launches/restarts the game and waits for its PID-specific pipe.

Mono compile references come from `<Game>_Data/Managed`. IL2CPP compile references come from `BepInEx/interop`. The cache key is based on compatibility inputs, not the game path, so multiple games can reuse one Agent when their referenced Unity binaries are identical.

The current strategy deliberately prefers exact binary compatibility over blindly treating all Unity versions as ABI-compatible. Mono therefore behaves like a shared compatibility-profile Agent rather than one universal binary across every Unity release. IL2CPP uses the same approach against generated Unity proxy assemblies. Moving more Unity API access behind runtime reflection/compatibility adapters can broaden the Mono reuse boundary later.

A user-selected running game is only asked to close normally. U3DViewer does not force-kill an existing user session. A temporary IL2CPP bootstrap process created by U3DViewer itself may be terminated after interop generation.

## Backend boundary

`U3DViewer.Protocol` does not reference Unity, BepInEx or Il2CppInterop types. Both agents translate runtime objects into plain `SceneSnapshot` DTOs before sending them to the Viewer.

Mono uses `BaseUnityPlugin.Update()` directly. IL2CPP uses `BasePlugin.Load()` and attaches a runtime behaviour so Unity API access still occurs on Unity's main thread.

The standalone Viewer therefore has one backend-neutral inspection UI and protocol.

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

Unity object APIs are accessed only on Unity's main thread. Pipe reader/writer threads never directly access Unity objects.

## Scene pixel path

Current M4 path targets Windows x64 + Direct3D 11:

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

The staging readback is a correctness-first implementation. The long-term Viewer should consume the shared GPU texture directly.

## Milestones

### M0-M3

Implemented foundations:

- Mono and IL2CPP Agent projects
- shared runtime-neutral protocol
- Runtime Hierarchy and Inspector
- PID-specific duplex Named Pipe
- isolated Scene Camera and keyboard controls
- startup process picker

### M4 — D3D11 Scene transport and automated preparation

Initial implementation present:

- runtime RenderTexture
- native shared D3D11 Texture2D
- named NT shared resource
- keyed-mutex synchronization
- Viewer shared-resource reader
- staging readback into Avalonia Scene View
- GUI `Attach`, `Prepare + Restart`, and `Open Game...`
- automatic BepInEx bootstrap when missing
- automatic IL2CPP interop bootstrap
- on-demand Agent build for a new compatibility profile
- compatibility-keyed Agent cache/reuse
- automatic deployment and Agent wait/connect

Still required before M4 is stable:

- real Mono runtime validation
- real IL2CPP runtime validation
- broader Unity-version compatibility testing
- resize negotiation
- device-loss/recreation recovery
- direct GPU presentation instead of staging readback

### M5 — scene tools

Planned:

- object picking
- Renderer bounds
- Collider visualization
- Camera frustums
- grid and transform gizmos

### M6 — runtime hardening

Planned:

- incremental hierarchy updates
- better IL2CPP component type resolution
- `DontDestroyOnLoad` / hidden object enumeration
- broader version-agnostic Mono compatibility adapter
- packaged/self-contained Agent compiler strategy that does not require a separately installed SDK for uncached profiles
- DX12/Vulkan transport backends after D3D11 is stable

## Current constraints

The current runtime path targets Windows x64, Unity Mono/IL2CPP, BepInEx 6 and Direct3D 11. It is intended for games the operator is authorized to inspect/debug. Runtime validation remains local/manual; the repository intentionally does not use GitHub Actions.
