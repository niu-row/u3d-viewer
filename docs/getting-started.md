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

Game-specific Viewer data stays under one directory beside the selected game executable:

```text
<Game>\U3DViewer\
├─ Settings\scene.json
├─ Downloads\
├─ AgentCache\<Backend>\<compatibility-fingerprint>\
├─ Temp\AgentBuilder\<Backend>-<ViewerPID>\
└─ Logs\viewer-*.log
```

Successful Agent builds are copied into `AgentCache` and their temporary `Temp/AgentBuilder` workspace is removed. A failed build keeps its workspace so the failure can be inspected.

Viewer-global data is kept beside `U3DViewer.Viewer.exe` instead of under the user profile:

```text
<Viewer>\U3DViewer\
├─ language.txt
└─ Logs\              # used before a game is selected
```

Once a game is selected, the active Viewer log moves into that game's `U3DViewer/Logs` directory.

## 4. Viewer layout

After connection, the main window contains:

- lazy Runtime Hierarchy
- read-only Runtime Inspector
- GPU-native Scene View
- Perspective / Orthographic controls
- a Scene Settings window for FOV, clipping planes, Scene FPS, resolution behavior, and culling
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

The Scene toolbar only keeps high-frequency actions. FOV, near/far clip planes, orthographic size, idle/active FPS, resolution behavior, and culling are configured from `Scene Settings`.

By default, the Scene RenderTexture follows the actual Scene View control size and aspect ratio. Resizing is debounced so the shared texture is recreated only after the resize settles. Fixed resolution remains available by disabling automatic viewport matching.

Default stream settings are:

```text
Idle FPS        15
Active FPS      30
Auto viewport   enabled
Manual fallback 1280 x 720
```

Scene settings are persisted per game in:

```text
<Game>\U3DViewer\Settings\scene.json
```

This includes lens values, Scene FPS, automatic/manual resolution settings, and culling mode/mask. Reattaching to the same game restores the saved profile automatically.

Changing resolution recreates the Unity RenderTexture and D3D11 shared texture. The Agent requests an immediate Scene-state snapshot after a stream change so the Viewer can switch to the new shared texture without waiting for the normal one-second snapshot cadence.

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

Viewer UI currently supports English and Simplified Chinese. The first launch follows the system UI language, and the selected language is persisted beside the Viewer executable:

```text
<Viewer>\U3DViewer\language.txt
```

Technical diagnostics, build output, exception messages, and Unity component/type names remain in their original form.

## Troubleshooting

Before a game is selected, Viewer diagnostics use:

```text
<Viewer>\U3DViewer\Logs\viewer-*.log
```

After selection, the active log is moved to:

```text
<Game>\U3DViewer\Logs\viewer-*.log
```

If automatic Agent compilation fails, the failed temporary workspace remains under:

```text
<Game>\U3DViewer\Temp\AgentBuilder\
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

Older builds used `%LOCALAPPDATA%\U3DViewer`. Current builds no longer write runtime data there. Existing legacy files are left untouched rather than being deleted automatically.

## Current limitations

- Windows x64 automatic runtime preparation
- Direct3D 11 Scene transport
- uncached Agent compilation currently uses the locally installed .NET SDK
- Hierarchy/Inspector metadata still uses Named Pipe + JSON
- picking, collider visualization, camera frustums, grid, and transform gizmos are not implemented yet
