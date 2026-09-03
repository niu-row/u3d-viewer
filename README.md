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
  -> builds the Agent against that game's Unity assemblies
  -> deploys Agent + Protocol + NativeBridge
  -> launches/restarts the game
  -> waits for u3d-viewer-<PID>
  -> opens Hierarchy / Inspector / Scene View
```

A running game selected through `Prepare + Restart` is only asked to close normally; U3DViewer does not force-kill an existing user session. A temporary IL2CPP bootstrap process launched by U3DViewer itself may be terminated after interop generation completes.

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

The Viewer-side Agent Builder copies its bundled source workspace to `%LOCALAPPDATA%/U3DViewer/AgentBuilder/`, then runs the local .NET SDK only after a target game has been selected. Mono references come from `<Game>_Data/Managed`; IL2CPP references come from `BepInEx/interop` after BepInEx generates them.

## Runtime architecture

```text
Unity Game.exe
├─ BepInEx 6
├─ U3DViewer.Agent.Mono.dll OR U3DViewer.Agent.IL2CPP.dll
├─ U3DViewer.Protocol.dll
└─ U3DViewer.NativeBridge.dll
        │
        ├─ Named Pipe: u3d-viewer-<PID>
        └─ D3D11 named shared Texture2D
                ▼
U3DViewer.Viewer.exe
├─ process picker
├─ automatic runtime preparation
├─ Runtime Hierarchy
├─ Runtime Inspector
└─ Scene View
```

## Current capabilities

- running Unity process discovery
- Mono / IL2CPP detection
- per-process Agent pipes
- GUI `Attach`, `Prepare + Restart`, and `Open Game...`
- automatic BepInEx 6 x64 bootstrap when absent
- automatic target-specific Agent build and deployment
- live Runtime Hierarchy
- read-only Runtime Inspector
- Scene Camera controls
- D3D11 Scene View transport through a named shared texture
- first Viewer presentation path through staging readback into Avalonia

## Current limitations

- Windows x64 first
- Scene transport currently requires Direct3D 11
- the development build currently expects a local .NET SDK for on-demand Agent compilation
- unusual/custom Unity launchers can require additional process discovery handling
- Scene View is fixed at 1280x720 and currently uses GPU-to-CPU staging readback
- picking, collider visualization and transform gizmos are not implemented yet
- no GitHub Actions; validation is local/manual

## Start here

- `docs/getting-started.md`
- `docs/architecture.md`
- `native/U3DViewer.NativeBridge/README.md`
