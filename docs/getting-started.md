# Getting started

The development workflow is now GUI-first. You do not configure a target game before building U3DViewer.

## Requirements

- Windows x64
- .NET 8 SDK
- Visual Studio 2022 C++ workload with CMake + Windows SDK
- a Unity game you are authorized to inspect/debug

The current Scene View transport requires Direct3D 11.

## 1. Build once

In VSCode, press:

```text
Ctrl+Shift+B
```

or run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1
```

This builds NativeBridge and Viewer. It also copies the Mono/IL2CPP Agent source projects plus Protocol project into the Viewer output as the runtime Agent Builder payload.

No `gamePath`, backend selection, Unity DLL copying, or BepInEx setup is required at this stage.

## 2. Start Viewer

Run:

```text
src/U3DViewer.Viewer/bin/Release/net8.0/U3DViewer.Viewer.exe
```

The first window lists detected Unity processes.

### Existing process

- `Ready` -> click `Attach`.
- `Not detected` -> click `Prepare + Restart`.
- `Busy` -> another Viewer already owns that Agent connection.

`Prepare + Restart` asks an existing game session to close normally. It does not force-kill a game the user was already running.

### Game not running

Click `Open Game...` and select the Unity game executable.

## 3. What the GUI prepares automatically

For Mono:

```text
select Game.exe
  -> detect Mono
  -> install BepInEx 6 x64 if missing
  -> use <Game>_Data/Managed as compile references
  -> build U3DViewer.Agent.Mono.dll on demand
  -> deploy plugin + protocol + NativeBridge
  -> launch game
  -> wait for u3d-viewer-<PID>
  -> open Viewer
```

For IL2CPP:

```text
select Game.exe
  -> detect IL2CPP
  -> install BepInEx 6 x64 if missing
  -> if BepInEx/interop is missing:
       launch a temporary bootstrap game process
       wait for interop generation
       stop that bootstrap process
  -> build U3DViewer.Agent.IL2CPP.dll against BepInEx/interop
  -> deploy plugin + protocol + NativeBridge
  -> launch game
  -> wait for u3d-viewer-<PID>
  -> open Viewer
```

The runtime bootstrap is pinned to BepInEx `6.0.0-be.785`, matching the Agent package references.

## 4. Expected Viewer

After connection, the main window contains:

- Runtime Hierarchy
- Runtime Inspector
- Scene View
- Reset / Perspective / Orthographic / Focus Selected
- WASD/QE movement and arrow-key camera look

## Troubleshooting

If automatic Agent compilation fails, the process picker shows the tail of `dotnet build` output. The temporary build workspace is under:

```text
%LOCALAPPDATA%\U3DViewer\AgentBuilder\
```

If BepInEx or IL2CPP bootstrap fails, inspect:

```text
<Game>\BepInEx\LogOutput.log
```

The main expected failure classes are:

- no write permission to the game directory
- no internet connection when BepInEx must be downloaded
- missing .NET SDK for on-demand Agent compilation
- unsupported/custom Unity executable layout
- target game is not running Direct3D 11 for Scene View

## Current limitations

- Windows x64 only for automatic runtime preparation
- D3D11 Scene transport only
- Agent compilation currently uses the locally installed .NET SDK
- Scene image path still uses staging readback
- picking, colliders and transform gizmos are not implemented yet
