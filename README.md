# u3d-viewer

Runtime scene inspection for already-built Unity games.

The project is split into a game-side runtime agent and a standalone viewer:

- **U3DViewer.Agent.Mono** runs inside an authorized Unity Mono game via BepInEx and reads the live Unity scene graph.
- **U3DViewer.Agent.IL2CPP** does the same for Unity IL2CPP games through BepInEx 6 + Il2CppInterop.
- **U3DViewer.Protocol** contains the runtime-neutral wire messages shared by both agents and the viewer.
- **U3DViewer.Viewer** runs as a separate process and receives live scene snapshots over a named pipe.
- A later **NativeBridge** milestone will transport a Scene Camera render target to the standalone viewer using D3D11 shared resources.

## Current milestone

Phase 0/1: prove the runtime hierarchy path on both Unity scripting backends before adding 3D rendering.

1. Load either the Mono or IL2CPP agent in a built Unity game.
2. Enumerate loaded scenes and GameObject hierarchy on the Unity main thread.
3. Send the same `SceneSnapshot` format to a standalone viewer through a named pipe.
4. Keep the viewer independent from Mono/IL2CPP runtime details.

## Initial scope

- Windows x64
- Unity Mono and IL2CPP
- BepInEx 6
- Read-only inspection
- Named Pipe IPC
- JSON messages
- Standalone console viewer first
- Avalonia UI and D3D11 Scene View after the data path is stable
- No GitHub Actions; validation is local/manual

## Start here

- `docs/getting-started.md` — Mono/IL2CPP build and first runtime test
- `docs/architecture.md` — target architecture and milestones
