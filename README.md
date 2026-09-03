# u3d-viewer

Runtime scene inspection for already-built Unity games.

The project is intentionally split into two sides:

- **U3DViewer.Agent.Mono** runs inside an authorized Unity Mono game via BepInEx and reads the live Unity scene graph.
- **U3DViewer.Viewer** runs as a separate process and receives scene snapshots over a named pipe.
- **U3DViewer.Protocol** contains the shared wire messages.
- A later **NativeBridge** milestone will transport a Scene Camera render target to the standalone viewer using D3D11 shared resources.

## Current milestone

Phase 0/1: prove the runtime data path before adding 3D rendering.

1. Load the Mono agent in a built Unity game.
2. Enumerate loaded scenes and GameObject hierarchy on the Unity main thread.
3. Send a scene snapshot to a standalone console viewer through a named pipe.
4. Keep the protocol independent from Unity types so Mono and IL2CPP agents can share it later.

## Initial scope

- Windows x64
- Unity Mono first
- BepInEx 6
- Read-only inspection
- Named Pipe IPC
- JSON messages
- Standalone console viewer first
- Avalonia UI and D3D11 Scene View after the data path is stable

## Start here

- `docs/getting-started.md` — build and first runtime test
- `docs/architecture.md` — target architecture and milestones

The current bootstrap lives in PR #1 until the first runtime hierarchy path is validated.
