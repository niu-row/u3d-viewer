# u3d-viewer

Runtime scene inspection for already-built Unity games.

The project is split into a game-side runtime agent and a standalone viewer:

- **U3DViewer.Agent.Mono** runs inside an authorized Unity Mono game via BepInEx and reads the live Unity scene graph.
- **U3DViewer.Agent.IL2CPP** does the same for Unity IL2CPP games through BepInEx 6 + Il2CppInterop.
- **U3DViewer.Protocol** contains the runtime-neutral wire messages shared by both agents and the viewer.
- **U3DViewer.Viewer** is a standalone .NET 8 + Avalonia desktop application.
- **U3DViewer.NativeBridge** transports the runtime Scene Camera through a named D3D11 shared texture.

## VSCode quick start

The repository includes `.vscode/tasks.json` plus local PowerShell build/deploy scripts.

Press `Ctrl+Shift+B` to run the default `U3DViewer: Build` task.

The default build no longer requires `u3dviewer.local.json` or a valid `gamePath`. It always builds:

```text
NativeBridge x64
  -> any Agent backend whose Unity references are already available
  -> Viewer
```

If `u3dviewer.local.json` contains a valid `backend` + `gamePath`, the build also stages that game's Unity reference assemblies when necessary and builds the matching Agent payload. If no usable Unity references are available, the build prints a warning and still produces Viewer + NativeBridge instead of failing.

When the Viewer is built after an Agent, that Agent DLL is bundled under `payload/Mono` or `payload/IL2CPP`. This enables GUI-side `Install + Restart` / `Open Game...` deployment for that backend.

`u3dviewer.local.json` is now optional for the normal GUI-first workflow. It is still useful when you want to prebuild a target-specific Agent payload or use the explicit `Deploy` tasks. Local config and build output are ignored by Git. For IL2CPP, BepInEx must have generated `BepInEx/interop` before a target-specific IL2CPP Agent can be built.

Other VSCode tasks include `Build + Deploy`, deploy-only, and `Build + Deploy + Run Viewer`.

## GUI launch / attach automation

`U3DViewer.Viewer.exe` starts with a Unity process picker instead of connecting to one global pipe.

The picker scans running Windows processes for Unity standalone layout markers such as `UnityPlayer.dll` / `<Game>_Data/globalgamemanagers`, detects Mono vs IL2CPP when possible, and reports the U3DViewer Agent state.

Each Agent owns a process-specific pipe:

```text
u3d-viewer-<PID>
```

The picker supports three normal workflows:

```text
Agent Ready
  -> Attach

Unity process + Agent Not detected
  -> Install + Restart
  -> copy bundled Agent + Protocol + NativeBridge
  -> request a graceful game close
  -> relaunch the game
  -> wait for u3d-viewer-<new PID>
  -> open Viewer automatically

Open Game...
  -> choose a Unity .exe
  -> detect Mono / IL2CPP
  -> deploy the bundled Agent
  -> launch the game
  -> wait for Agent Ready
  -> open Viewer automatically
```

`Install + Restart` does not force-kill the game. If the process refuses a graceful close, U3DViewer leaves it running and asks the user to close it manually. The GUI automation also does not perform generic remote DLL injection into an arbitrary live process.

A matching BepInEx 6 runtime must already be installed in the target game. If BepInEx is absent, the picker reports that GUI installation is unavailable rather than silently installing a runtime build that may not match the game.

This allows multiple Unity games to run at the same time without their Viewer connections colliding. An Agent already occupied by another Viewer is shown as `Busy`.

## Current milestone

M2 desktop UI and M3 Scene Camera control are implemented. M4 now has an initial end-to-end Scene View transport implementation ready for local runtime validation.

Implemented:

- startup Unity process picker with PID/backend/Agent status
- GUI `Attach`, `Install + Restart`, and `Open Game...` workflows
- Agent payload bundled into the Viewer output for GUI deployment
- per-process Named Pipe connection (`u3d-viewer-<PID>`)
- automatic connection/reconnection to the selected Mono or IL2CPP agent
- live Runtime Hierarchy tree
- selection that survives ordinary snapshot refreshes
- read-only Runtime Inspector for GameObject state, Transform and component type names
- bidirectional Named Pipe control channel
- isolated runtime Scene Camera controller in both Mono and IL2CPP agents
- camera reset, perspective/orthographic switch, focus selected, keyboard move/look commands
- 1280x720 runtime RenderTexture rendered by the target game
- D3D11 named shared texture transport with keyed-mutex synchronization
- render-target metadata published through `SceneSnapshot.RenderTarget`
- Viewer-side opening of the named D3D11 texture
- first live Scene View presentation path through a staging readback into an Avalonia `WriteableBitmap`
- connection, snapshot and Scene transport status

The first M4 Viewer path intentionally uses GPU-to-CPU staging readback. It is meant to prove the complete pipeline against real games before replacing the readback with direct GPU presentation.

## Scope

- Windows x64 first
- Unity Mono and IL2CPP
- BepInEx 6
- Direct3D 11 Scene transport first
- read-only inspection first
- Named Pipe for runtime metadata/control
- D3D11 shared resource for Scene View pixels
- standalone Viewer independent from the target game's Unity version
- no GitHub Actions; validation is local/manual

## Runtime render path

```text
Built Unity game
  -> Mono or IL2CPP Agent
  -> Scene Camera
  -> RenderTexture
  -> U3DViewer.NativeBridge.dll
  -> named D3D11 shared Texture2D
  -> U3DViewer.Viewer.exe
  -> Avalonia Scene View
```

## Start here

- `docs/getting-started.md` — Mono/IL2CPP build and first runtime test
- `docs/architecture.md` — target architecture and milestones
- `native/U3DViewer.NativeBridge/README.md` — native bridge build/deployment and M4 limitations
