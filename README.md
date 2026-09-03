# u3d-viewer

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

## Diagnostics

The development Viewer is built as a console application in addition to the Avalonia GUI. Keep the console window open while preparing or attaching to a game.

Runtime preparation messages, resolved Unity reference paths, compatibility fingerprints, Agent cache hits, Agent build stdout/stderr, Scene GPU adapter identity, and native Scene presentation failures are written to the console.

The same diagnostic stream is persisted under:

```text
%LOCALAPPDATA%/U3DViewer/Logs/
  viewer-YYYYMMDD-HHMMSS-<PID>.log
```

Unhandled Viewer exceptions are also written there. `BepInEx/LogOutput.log` remains the game-side source for BepInEx/Agent load errors.

## Agent reuse and cache

Agents are not rebuilt on every launch.

U3DViewer stores compatible builds under:

```text
%LOCALAPPDATA%/U3DViewer/AgentCache/
  Mono/<compatibility-fingerprint>/
  IL2CPP/<compatibility-fingerprint>/
```

The fingerprint includes the selected backend, the bundled Agent/Protocol builder inputs, and SHA-256 hashes of the Unity assemblies that the Agent compiles against.

This means:

- reopening the same game normally uses the cache;
- two games with identical compatible Unity assemblies can share the same Mono Agent cache entry;
- two IL2CPP targets with identical generated Unity proxy assemblies can share the same IL2CPP cache entry;
- a Unity/interop update automatically produces a new cache key;
- changing U3DViewer Agent or Protocol source automatically invalidates the old cache.

This is compatibility-based reuse rather than blindly loading one binary across every Unity version. A broader truly version-agnostic Mono Agent can be pursued later by moving more Unity API access behind runtime compatibility/reflection adapters.

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

The Viewer-side Agent Builder copies its bundled source workspace to `%LOCALAPPDATA%/U3DViewer/AgentBuilder/` only when a compatibility profile has no cache entry. Mono references come from `<Game>_Data/Managed`; IL2CPP references come from `BepInEx/interop` after BepInEx generates them.

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
├─ process picker
├─ automatic runtime preparation
├─ compatibility Agent cache
├─ Runtime Hierarchy
├─ Runtime Inspector
└─ Scene View
   └─ Avalonia NativeControlHost
      └─ Win32 child HWND
         └─ D3D11 swap chain
            └─ samples the named shared texture directly on the game GPU adapter
```

The active Scene presentation path does not perform GPU-to-CPU staging readback. The Viewer opens the named shared texture on the same DXGI adapter LUID, samples it in a small D3D11 shader, performs the Unity RenderTexture Y flip on the GPU, and presents directly into the embedded HWND swap chain.

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

The Scene Camera renders at about 30 FPS while idle and temporarily boosts toward 60 FPS while move/look commands are active. This keeps navigation responsive without continuously doubling the extra Camera.Render workload.

## Current capabilities

- running Unity process discovery
- Mono / IL2CPP detection
- per-process Agent pipes
- GUI `Attach`, `Prepare + Restart`, and `Open Game...`
- automatic BepInEx 6 x64 bootstrap when absent
- automatic target-compatible Agent build and deployment
- compatibility-keyed Agent reuse/cache
- live Runtime Hierarchy
- read-only Runtime Inspector
- Unity-style Scene fly camera controls
- D3D11 Scene View transport through a named shared texture
- game/Viewer DXGI adapter LUID diagnostics
- GPU-native Viewer Scene presentation with no CPU readback
- GPU-side Y flip and aspect-preserving presentation

## Current limitations

- Windows x64 first
- Scene transport currently requires Direct3D 11
- a new, uncached compatibility profile currently requires a local .NET SDK for Agent compilation
- unusual/custom Unity launchers can require additional process discovery handling
- Scene Camera source render target is currently fixed at 1280x720
- the game still performs an extra Scene Camera render for the inspector view; interactive rendering boosts toward 60 FPS and idle rendering falls back to about 30 FPS
- picking, collider visualization and transform gizmos are not implemented yet
- no GitHub Actions; validation is local/manual

## Start here

- `docs/getting-started.md`
- `docs/architecture.md`
- `native/U3DViewer.NativeBridge/README.md`
