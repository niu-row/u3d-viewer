# Getting started

The development workflow is GUI-first. You do not configure a target game before building U3DViewer.

## Requirements

- Windows x64
- .NET 8 SDK
- Visual Studio 2022 C++ workload with CMake + Windows SDK
- a Unity game you are authorized to inspect/debug

The current Scene View transport requires Direct3D 11.

## 1. Build

In VSCode, press:

```text
Ctrl+Shift+B
```

or run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1
```

This builds NativeBridge and Viewer. It also recreates the bundled Mono/IL2CPP Agent + Protocol source payload beside the Viewer for compatibility-specific runtime Agent builds.

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

## 3. Automatic runtime preparation

For Mono:

```text
select Game.exe
  -> detect Mono
  -> install BepInEx 6 x64 if missing
  -> fingerprint target compatibility
  -> reuse cached Agent when possible
  -> otherwise build U3DViewer.Agent.Mono.dll on demand
  -> deploy Agent + Protocol + NativeBridge
  -> launch game
  -> wait for u3d-viewer-<PID>
  -> open Viewer
```

For IL2CPP:

```text
select Game.exe
  -> detect IL2CPP
  -> install BepInEx 6 x64 if missing
  -> if required interop assemblies are incomplete:
       launch a temporary bootstrap game process
       wait for Unity core + scene + Il2Cppmscorlib references
       stop that bootstrap process
  -> fingerprint target compatibility
  -> reuse cached Agent when possible
  -> otherwise build U3DViewer.Agent.IL2CPP.dll against BepInEx/interop
  -> deploy Agent + Protocol + NativeBridge
  -> launch game
  -> wait for u3d-viewer-<PID>
  -> open Viewer
```

The runtime bootstrap is pinned to BepInEx `6.0.0-be.785`, matching the Agent package references.

Agent builds are cached under:

```text
%LOCALAPPDATA%\U3DViewer\AgentCache\<Backend>\<compatibility-fingerprint>\
```

## 4. Viewer layout

After connection, the main window contains:

- lazy Runtime Hierarchy
- read-only Runtime Inspector
- GPU-native Scene View
- Perspective / Orthographic controls
- adjustable FOV, near/far clip planes, and orthographic size
- adjustable idle/active Scene FPS and RenderTexture resolution
- Scene Camera culling mask modes: All / Copy Main Camera / Manual Layers
- visible fly-camera speed
- runtime performance metrics

Hierarchy discovery is lazy. Scene roots are loaded first; child branches are scanned only when expanded. Inspector-heavy data is read only for the selected object.

## Scene View controls

```text
RMB + mouse     free look (Raw Input)
RMB + W/S       forward / backward
RMB + A/D       left / right
RMB + Q/E       down / up
Shift           temporary movement boost
Mouse wheel     adjust fly speed
F               focus selected GameObject
```

Default stream settings are:

```text
Idle FPS        15
Active FPS      30
Width           1280
Height          720
```

All four values are adjustable in the Viewer. Changing resolution recreates the Unity RenderTexture and D3D11 shared texture.

The Scene pixel path is GPU-native:

```text
Unity Camera.Render()
  -> RenderTexture
  -> named D3D11 shared Texture2D
  -> keyed mutex
  -> Viewer D3D11 shader
  -> embedded HWND swap chain
```

There is no active CPU staging readback / `WriteableBitmap` path.

## Language

Viewer UI currently supports English and Simplified Chinese. The first launch follows the system UI language, and the selected language is persisted under:

```text
%LOCALAPPDATA%\U3DViewer\language.txt
```

Technical diagnostics, build output, exception messages, and Unity component/type names remain in their original form.

## Troubleshooting

Viewer diagnostics are written both to the development console and:

```text
%LOCALAPPDATA%\U3DViewer\Logs\viewer-*.log
```

If automatic Agent compilation fails, inspect the build output there. Temporary build workspaces are under:

```text
%LOCALAPPDATA%\U3DViewer\AgentBuilder\
```

If BepInEx or Agent loading fails, inspect:

```text
<Game>\BepInEx\LogOutput.log
```

Common failure classes include:

- no write permission to the game directory
- no internet connection when BepInEx must be downloaded
- missing .NET SDK for an uncached compatibility profile
- unsupported/custom Unity executable layout
- target game not running Direct3D 11 for Scene View
- game and Viewer unable to open the shared resource on the same DXGI adapter

## Current limitations

- Windows x64 automatic runtime preparation
- Direct3D 11 Scene transport
- uncached Agent compilation currently uses the locally installed .NET SDK
- Hierarchy/Inspector metadata still uses Named Pipe + JSON
- picking, collider visualization, camera frustums, grid, and transform gizmos are not implemented yet
