# u3d-viewer

[English](README.md) | [简体中文](README.zh-CN.md)

Standalone runtime inspection for already-built Unity games.

## User flow

The normal workflow is GUI-first. There is no required `gamePath` or `backend` config file.

```text
Ctrl+Shift+B
  -> build Viewer + NativeBridge + bundled Agent Builder sources

U3DViewer.Viewer.exe
  -> choose a running Unity process
       Ready              -> Attach
       Agent not detected -> Prepare + Restart
  OR Open Game...
       -> choose Game.exe

U3DViewer then automatically:
  -> detects Mono / IL2CPP
  -> installs the pinned matching BepInEx 6 x64 runtime when missing
  -> for IL2CPP, starts the game once when interop assemblies need generation
  -> fingerprints the compatible Unity/interop API surface
  -> reuses a cached Agent when that compatibility profile was built before
  -> otherwise builds the Agent once and caches it
  -> deploys Agent + Protocol + NativeBridge
  -> launches/restarts the game
  -> waits for u3d-viewer-<PID>
  -> opens Hierarchy / Inspector / Scene View
```

A running game selected through `Prepare + Restart` is only asked to close normally; U3DViewer does not force-kill an existing user session. A temporary IL2CPP bootstrap process launched by U3DViewer itself may be terminated after interop generation completes.

## Runtime storage

Viewer-global data is kept beside `U3DViewer.Viewer.exe`:

```text
<Viewer>\U3DViewer\
├─ language.txt
└─ Logs\
```

Game-specific Viewer data is kept beside the selected game executable:

```text
<Game>\U3DViewer\
├─ Settings\scene.json
├─ Downloads\
├─ AgentCache\<Backend>\<compatibility-fingerprint>\
├─ Temp\AgentBuilder\<Backend>-<ViewerPID>\
└─ Logs\viewer-*.log
```

The active Viewer log moves into the selected game's `U3DViewer/Logs` directory. Successful Agent builds are copied into `AgentCache` and their temporary workspace is deleted. Failed build workspaces remain for diagnosis.

Older versions used `%LOCALAPPDATA%\U3DViewer`. Current builds no longer write runtime data there; existing legacy files are left untouched.

## Languages

The Viewer currently supports English and Simplified Chinese. The first launch follows the operating-system UI language (`zh-*` selects Simplified Chinese; other languages fall back to English), and the language can be changed from the selector at the top of the Viewer. The selection is persisted at `<Viewer>/U3DViewer/language.txt`.

Diagnostic logs, build output, exception messages, Unity type names, and third-party runtime messages remain in their original technical form so errors remain searchable.

## Diagnostics

The development Viewer is built as a console application in addition to the Avalonia GUI. Keep the console window open while preparing or attaching to a game.

Runtime preparation messages, resolved Unity reference paths, compatibility fingerprints, Agent cache hits, Agent build stdout/stderr, Scene GPU adapter identity, and native Scene presentation failures are written to the console.

Before a game is selected, the file log is under `<Viewer>/U3DViewer/Logs`. After selection it moves to:

```text
<Game>\U3DViewer\Logs\viewer-YYYYMMDD-HHMMSS-<PID>.log
```

Unhandled Viewer exceptions are also written there. `BepInEx/LogOutput.log` remains the game-side source for BepInEx/Agent load errors.

## Agent reuse and cache

Agents are not rebuilt on every launch.

Each game keeps compatible builds under:

```text
<Game>\U3DViewer\AgentCache\
  Mono/<compatibility-fingerprint>/
  IL2CPP/<compatibility-fingerprint>/
```

The fingerprint includes the selected backend, the bundled Agent/Protocol builder inputs, and SHA-256 hashes of the Unity assemblies that the Agent compiles against.

This means reopening the same game normally uses its cache; a Unity/interop update or a change to U3DViewer Agent/Protocol source automatically produces a new cache key.

## Build in VSCode

Requirements for building the development checkout:

- Windows x64
- .NET 8 SDK
- Visual Studio 2022 C++ workload with Windows SDK and CMake

Press `Ctrl+Shift+B`. The default task runs `scripts/build.ps1` and builds:

```text
build/native/Release/U3DViewer.NativeBridge.dll
src/U3DViewer.Viewer/bin/Release/net8.0/U3DViewer.Viewer.exe
src/U3DViewer.Viewer/bin/Release/net8.0/agent-builder/...
```

No target game path is needed during this build.

The Viewer-side Agent Builder copies its bundled source workspace under `<Game>/U3DViewer/Temp/AgentBuilder/` only when that game's compatibility profile has no cache entry. Mono uses the BepInEx Unity facade plus target compatibility inputs; IL2CPP references come from `BepInEx/interop` after BepInEx generates the required proxy assemblies.

A local .NET SDK is therefore required for the first build of a new compatibility profile. Cache hits do not invoke `dotnet build`.

## Runtime architecture

```text
Unity Game.exe
├─ BepInEx 6
├─ U3DViewer.Agent.Mono.dll OR U3DViewer.Agent.IL2CPP.dll
├─ U3DViewer.Protocol.dll
└─ U3DViewer.NativeBridge.dll
        │
        ├─ Named Pipe: u3d-viewer-<PID>
        └─ D3D11 named shared Texture2D + keyed mutex
                ▼
U3DViewer.Viewer.exe
├─ process picker / Open Game
├─ automatic runtime preparation
├─ per-game compatibility Agent cache
├─ lazy Runtime Hierarchy
├─ Runtime Inspector
└─ Scene View
   └─ Avalonia NativeControlHost
      └─ Win32 child HWND
         └─ dedicated presenter thread
            └─ D3D11 swap chain
               └─ samples the named shared texture directly on the game GPU adapter
```

The active Scene presentation path does not perform GPU-to-CPU staging readback. The Viewer opens the named shared texture on the same DXGI adapter LUID, samples it in a small D3D11 shader, performs the Unity RenderTexture Y flip on the GPU, and presents directly into the embedded HWND swap chain.

Open/close/present calls are serialized on one dedicated Viewer presenter thread. Interactive window sizing pauses presentation and recreates the presenter after the final size settles. The hot `Present` path does not call `ResizeBuffers`; DXGI may temporarily stretch the existing backbuffer while the HWND is changing size, then a fresh swap chain is opened at the final dimensions.

Hierarchy discovery is lazy: scene roots are loaded first and child branches are scanned only when expanded. Unity API work remains on the Unity main thread and is spread across frames with a small per-frame budget. Snapshot JSON serialization runs off the Unity main thread.

## Scene View controls

The Scene host uses native Win32 input so camera navigation does not depend on keyboard auto-repeat or Avalonia pointer boundaries.

```text
RMB + mouse     free look (Raw Input)
RMB + W/S       forward / backward
RMB + A/D       left / right
RMB + Q/E       down / up
Shift           temporary movement boost
Mouse wheel     adjust fly speed
F               focus selected GameObject
```

The current fly speed is shown in the Scene toolbar and updates immediately when the mouse wheel changes it. Perspective and Orthographic are mutually exclusive toolbar states and follow the actual Agent projection state.

The Scene toolbar also provides independent `Follow Position` and `Follow Rotation` toggles. When enabled, the Scene Camera copies only the selected transform component from the current `Camera.main` immediately before a Scene render. Enabling both follows the full main-camera transform while leaving FOV, projection, clipping planes, and culling under U3DViewer control. These toggles are persisted per game.

The Agent can create the Scene Camera before the game's `Camera.main` is ready. After the first usable render target arrives, the Viewer automatically sends one camera reset so initial position/orientation is synchronized without requiring a manual Reset Camera click.

The main Scene toolbar keeps only high-frequency controls. Lens, stream, resolution, and culling settings live in the separate Scene Settings window. Toolbar content wraps on narrow layouts instead of forcing one long horizontal row.

Default stream behavior is:

```text
Idle FPS        15
Active FPS      30
Auto viewport   enabled
Manual fallback 1280 x 720
```

With automatic viewport matching enabled, the Unity RenderTexture follows the actual Scene View width, height, and aspect ratio. Viewer resize events are debounced before recreating the shared texture. Disable automatic matching to use a fixed width/height from 64 to 4096.

Scene settings are persisted per game in `<Game>/U3DViewer/Settings/scene.json`. The saved profile includes FOV, near/far clipping planes, orthographic size, idle/active FPS, automatic/manual resolution settings, culling mode/mask, and the two main-camera follow toggles.

Changing Scene resolution recreates the Unity RenderTexture and D3D11 shared texture. The Agent immediately refreshes Scene target state after a stream change so the Viewer does not wait for the normal one-second snapshot cadence before opening the new shared texture.

Scene Camera culling is configurable independently of the game view. `All` renders all 32 Unity Layers, `Copy Main Camera` follows the current `Camera.main.cullingMask` at snapshot cadence, and `Manual` opens a 32-Layer checklist using the target game's Layer names. `Copy Main Camera` remains the default when no per-game profile exists.

The Hierarchy and Inspector columns are resizable with splitters so the Scene View can take the remaining workspace width.

## Performance metrics

The Scene footer reports lightweight current metrics:

- actual Unity game FPS over a short rolling window;
- actual U3DViewer Scene Camera render FPS;
- Scene `Camera.Render()` CPU-side submission time;
- lazy Hierarchy nodes scanned and scan time;
- snapshot JSON serialization time;
- snapshot UTF-8 payload size.

The Agent also retains average/maximum timing counters internally. The Scene render metric is CPU-side timing around `Camera.Render()` and the native copy event submission; it is not a GPU timestamp query.

## Current capabilities

- running Unity process discovery
- Mono / IL2CPP detection
- per-process Agent pipes
- GUI `Attach`, `Prepare + Restart`, and `Open Game...`
- automatic BepInEx 6 x64 bootstrap when absent
- automatic target-compatible Agent build and deployment
- per-game compatibility-keyed Agent cache
- per-game persistent Scene settings
- lazy live Runtime Hierarchy
- read-only Runtime Inspector
- resizable Hierarchy / Scene / Inspector workspace
- Unity-style Scene fly camera controls
- adjustable Perspective/Orthographic Scene lens with visible active projection state
- automatic initial main-camera pose reset
- independent follow-main-camera position / rotation toggles
- automatic free-aspect Scene RenderTexture sizing or fixed manual resolution
- adjustable Scene FPS
- selectable Scene Camera culling mask: All / Copy Main Camera / Manual Layers
- visible fly-camera speed
- game FPS / Scene FPS / runtime performance metrics
- D3D11 Scene View transport through a named shared texture
- GPU-native Viewer Scene presentation with no CPU readback
- dedicated Scene presenter thread and resize recovery
- GPU-side Y flip and aspect-preserving presentation
- English / Simplified Chinese Viewer UI

## Current limitations

- Windows x64 first
- Scene transport currently requires Direct3D 11
- a new, uncached compatibility profile currently requires a local .NET SDK for Agent compilation
- unusual/custom Unity launchers can require additional process discovery handling
- the game still performs an extra Scene Camera render for the inspector view; use the adjustable FPS/resolution settings to balance load
- Hierarchy/Inspector control data currently uses JSON over the named pipe; GPU Scene pixels remain on the D3D11 shared-texture path
- picking, collider visualization and transform gizmos are not implemented yet
- no GitHub Actions; validation is local/manual

## Start here

- `docs/getting-started.md`
- `docs/architecture.md`
- `native/U3DViewer.NativeBridge/README.md`