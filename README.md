# u3d-viewer

Runtime scene inspection for already-built Unity games.

The project is split into a game-side runtime agent and a standalone viewer:

- **U3DViewer.Agent.Mono** runs inside an authorized Unity Mono game via BepInEx and reads the live Unity scene graph.
- **U3DViewer.Agent.IL2CPP** does the same for Unity IL2CPP games through BepInEx 6 + Il2CppInterop.
- **U3DViewer.Protocol** contains the runtime-neutral wire messages shared by both agents and the viewer.
- **U3DViewer.Viewer** is a standalone .NET 8 + Avalonia desktop application.
- A later **NativeBridge** milestone will transport a Scene Camera render target to the standalone viewer using D3D11 shared resources.

## Current milestone

M2 desktop UI is implemented and M3 Scene Camera control is now being wired on top of the M0/M1 runtime hierarchy pipeline.

Implemented:

- automatic Named Pipe connection/reconnection to either Mono or IL2CPP agent
- live Runtime Hierarchy tree
- selection that survives ordinary snapshot refreshes
- read-only Runtime Inspector for GameObject state, Transform and component type names
- bidirectional Named Pipe control channel
- isolated runtime Scene Camera controller in both Mono and IL2CPP agents
- camera reset, perspective/orthographic switch, focus selected, keyboard move/look commands
- connection and snapshot status
- reserved Scene View panel for the upcoming render transport

The camera currently remains disabled for rendering. M4 will attach a render target and transport it to the standalone Viewer with a D3D11 shared resource.

## Scope

- Windows x64 first
- Unity Mono and IL2CPP
- BepInEx 6
- read-only inspection first
- Named Pipe for runtime metadata/control
- D3D11 shared resource planned for Scene View pixels
- standalone Viewer independent from the target game's Unity version
- no GitHub Actions; validation is local/manual

## Start here

- `docs/getting-started.md` — Mono/IL2CPP build and first runtime test
- `docs/architecture.md` — target architecture and milestones
