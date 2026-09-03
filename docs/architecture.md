# Architecture

## Goal

Observe and inspect the live scene of an already-built Unity game from a standalone Viewer, with target preparation driven from the GUI instead of per-game configuration files.

```text
U3DViewer.Viewer.exe
├─ Process Picker / Open Game
├─ Runtime Preparation
│  ├─ detect Mono / IL2CPP
│  ├─ ensure BepInEx 6 x64
│  ├─ generate IL2CPP interop when required
│  ├─ fingerprint target compatibility
│  ├─ reuse cached Agent OR build/cache once
│  └─ deploy / launch / wait for Agent
├─ lazy Runtime Hierarchy
├─ Runtime Inspector
└─ Scene View
          │
          ▼
Unity game process
├─ U3DViewer.Agent.Mono OR U3DViewer.Agent.IL2CPP
├─ Named Pipe: u3d-viewer-<PID>
└─ D3D11 named shared Texture2D + keyed mutex
```

## Runtime preparation boundary

The normal development build does not know a target game. `Ctrl+Shift+B` builds Viewer + NativeBridge and recreates the Agent/Protocol source payload under `agent-builder/` beside the Viewer.

After the user selects a game, the Viewer:

1. detects the Unity backend from the game layout;
2. installs the pinned BepInEx 6 x64 runtime when missing;
3. for IL2CPP, launches a temporary bootstrap process when required interop assemblies are incomplete;
4. waits until the Unity core, scene, and `Il2Cppmscorlib` references required by the Agent are actually resolvable;
5. computes a compatibility fingerprint from the backend, bundled Agent/Protocol builder inputs, and SHA-256 hashes of compile-time Unity/interop references;
6. reuses `%LOCALAPPDATA%/U3DViewer/AgentCache/<backend>/<fingerprint>/` when available;
7. otherwise copies the bundled source workspace to `%LOCALAPPDATA%/U3DViewer/AgentBuilder/`, invokes the local .NET SDK with dynamically resolved references, and caches the resulting Agent + Protocol;
8. deploys Agent + Protocol + NativeBridge;
9. launches/restarts the game and waits for its PID-specific pipe.

A user-selected running game is only asked to close normally. U3DViewer does not force-kill an existing user session. A temporary IL2CPP bootstrap process created by U3DViewer itself may be terminated after interop generation completes.

## Backend boundary

`U3DViewer.Protocol` contains only runtime-neutral DTOs and command encoding. It does not reference Unity, BepInEx, or Il2CppInterop types.

Mono uses `BaseUnityPlugin.Update()` directly. IL2CPP uses `BasePlugin.Load()` plus a runtime `MonoBehaviour`. Unity APIs are accessed only from Unity's main thread in both backends.

The Mono and IL2CPP Agents intentionally remain separate compatibility targets. Their runtime logic is closely mirrored, but the different BepInEx/interop environments make aggressive source unification a compatibility risk until broader runtime validation exists.

## Connection lifetime

The Agent creates one PID-specific duplex Named Pipe:

```text
u3d-viewer-<PID>
```

The Agent performs no Scene Camera rendering or Hierarchy scanning while no Viewer is connected. `Application.runInBackground` is forced on while the Agent is loaded so switching focus to the standalone Viewer does not stop Unity's player loop; the original value is restored when the Agent unloads.

Viewer camera/control writes use a bounded asynchronous channel so Avalonia's UI thread never blocks on pipe I/O.

## Control and metadata path

Agent -> Viewer sends complete snapshots for the currently requested lazy tree surface:

```text
SceneSnapshot
├─ Scenes / lazy GameObject hierarchy
├─ selected-object Inspector data
├─ RenderTargetInfo
│  ├─ shared texture identity
│  ├─ adapter/DXGI data
│  ├─ projection/lens state
│  ├─ Scene FPS/resolution state
│  ├─ fly speed
│  └─ culling-mask mode/mask/layer names
└─ PerformanceInfo
```

Viewer -> Agent commands currently include:

```text
camera.move
camera.look
camera.speed
camera.projection
camera.lens
camera.stream
camera.culling
camera.reset
camera.focus
selection.set
hierarchy.expanded
```

Metadata currently uses newline-delimited JSON over the Named Pipe. JSON serialization runs off the Unity main thread. A binary/shared-memory metadata transport is optional future work only if measured payload/deserialize costs justify it.

## Lazy Hierarchy path

Hierarchy optimization happens before serialization rather than through a complex post-scan delta protocol.

Initial state:

```text
Scene -> root GameObjects only
```

When a user expands a node:

```text
Viewer
  -> hierarchy.expanded <instanceId> true
Agent
  -> scan only the newly requested branch
```

Unexpanded descendants are represented by `ChildCount` without recursively reading their children. The Viewer keeps already loaded child objects locally so collapse/re-expand is fast.

Unity-side scans are spread across frames:

```text
normal scan       <= 64 nodes / slice, about 0.75 ms budget
interactive scan  <= 256 nodes / slice, about 2.0 ms budget
```

Interactive mode is temporary and is used after explicit expand/selection operations. Inspector-heavy fields such as Components, Tag, and Transform details are collected only for the selected GameObject.

## Scene Camera

The Agent creates an isolated `__U3DViewerCamera` with its own RenderTexture. The Viewer controls this camera independently of the game's output camera.

Current adjustable state includes:

- Perspective / Orthographic projection
- FOV
- near/far clip planes
- orthographic size
- fly speed
- idle Scene FPS
- interactive Scene FPS
- RenderTexture width/height
- culling mask

Default stream values are:

```text
Idle FPS        15
Active FPS      30
Width           1280
Height          720
```

All are runtime settings rather than fixed transport constants.

### Culling mask

Three modes are supported:

```text
All
  -> cullingMask = 0xFFFFFFFF

Copy Main Camera
  -> follows Camera.main.cullingMask at snapshot cadence
  -> falls back to All when no usable Camera.main exists

Manual
  -> explicit 32-bit Layer mask selected in the Viewer
```

The Agent also publishes the game's 32 Unity Layer names for the manual selector.

## Scene pixel path

Current transport targets Windows x64 + Direct3D 11:

```text
Target game
  SceneCamera.Render()
    -> RenderTexture
    -> GetNativeTexturePtr()
    -> NativeBridge writer
    -> GPU CopyResource into named shared Texture2D
    -> D3D11_RESOURCE_MISC_SHARED_NTHANDLE
    -> IDXGIKeyedMutex (writer key 0 -> 1)

Named resource + source adapter LUID
              |
              v
Viewer
  Avalonia NativeControlHost
    -> Win32 child HWND
    -> D3D11 device created on source DXGI adapter
    -> OpenSharedResourceByName
    -> IDXGIKeyedMutex (reader key 1 -> 0)
    -> ShaderResourceView
    -> fullscreen shader
       - Unity RenderTexture Y flip on GPU
       - aspect-preserving viewport / letterbox
    -> DXGI swap chain
    -> Present
```

There is no active GPU-to-CPU staging readback or `WriteableBitmap` Scene path. The legacy native CPU reader has been removed. `NativeBridge.cpp` owns the target-process writer; `ScenePresenter.cpp` owns the Viewer GPU presenter and native input host.

Changing Scene resolution recreates the RenderTexture and shared resource with a generation-specific name so the Viewer reopens the correct native object.

## Scene input path

The embedded child HWND receives native Win32 input directly:

```text
RMB
  -> capture child HWND
  -> hide cursor
  -> Raw Input mouse deltas
  -> camera.look

RMB + WASD/QE
  -> key-state sampling
  -> normalized movement vector
  -> camera.move

Shift
  -> temporary movement boost

Mouse wheel
  -> camera.speed

F
  -> camera.focus
```

The Viewer displays the current fly speed and updates it immediately after wheel input.

## Performance metrics

The Agent publishes lightweight metrics with snapshots:

- Scene `Camera.Render()` CPU-side submission time: last / average / maximum;
- lazy Hierarchy scanned nodes and scan time: last / average / maximum;
- JSON serialization time;
- UTF-8 snapshot payload size.

The Scene Render metric is CPU timing around `Camera.Render()` plus native copy-event submission, not a D3D11 GPU timestamp.

## Viewer responsibilities

The Viewer currently contains four broad areas:

```text
Runtime preparation
  AgentBuilder / BepInExBootstrap / GameAutomation / process discovery

Connection + protocol consumption
  ViewerConnection

Runtime inspection UI
  MainWindow / Hierarchy / Inspector / Scene settings

Native Scene presentation
  NativeSceneHost / SceneCullingUi / Localization
```

The functional boundaries are sound, but `MainWindow` and the current localization/culling UI adapters are the main areas that should be decomposed before many more Scene tools are added.

## Current constraints

- Windows x64 first
- Direct3D 11 Scene transport
- uncached Agent profiles require a local .NET SDK
- metadata still uses Named Pipe + JSON
- unusual/custom Unity launchers can require extra process-discovery handling
- picking, Renderer bounds, Collider visualization, Camera frustums, grid, and transform gizmos are not implemented
- runtime validation is local/manual; the repository intentionally does not use GitHub Actions

## Next structural work

Prefer small, measured changes rather than another large transport rewrite:

1. split `MainWindow` into Hierarchy, Inspector, and Scene panel/controller components before adding more tools;
2. replace periodic visual-tree localization rewriting with explicit localized bindings/state;
3. move Scene culling controls into the Scene panel instead of wrapping the whole window;
4. remove duplicated Agent code only where the Mono and IL2CPP compile/runtime surface is demonstrably identical;
5. pause or reduce Scene presentation/rendering when the Scene surface is not visible;
6. consider binary/shared-memory metadata only if performance metrics show JSON is material.
