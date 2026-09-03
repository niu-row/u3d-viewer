# Getting started

The current bootstrap supports both Unity Mono and IL2CPP agents, an Avalonia standalone Viewer, a startup Unity process picker, GUI install/launch automation, bidirectional Scene Camera control, and an initial Windows/D3D11 live Scene View transport.

## Requirements

- Windows x64
- .NET 8 SDK for the standalone Viewer
- .NET 6 SDK for the IL2CPP agent
- Visual Studio C++ workload + CMake for `U3DViewer.NativeBridge.dll`
- A Unity game you are authorized to inspect/debug
- The matching BepInEx 6 runtime for that game
- The target game must run Direct3D 11 for the current Scene View transport

BepInEx must already be installed in a target game before the Viewer can use `Install + Restart` or `Open Game...`. U3DViewer does not currently install BepInEx automatically because the correct runtime distribution depends on the target backend/game.

## 1. Build the native D3D11 bridge

From a Visual Studio x64 developer shell:

```powershell
cmake -S native/U3DViewer.NativeBridge -B build/native -A x64
cmake --build build/native --config Release
```

The output is:

```text
build/native/Release/U3DViewer.NativeBridge.dll
```

The Viewer build copies this DLL next to `U3DViewer.Viewer.exe` when it exists under `build/native/<Configuration>`.

## 2. Mono setup

For a Unity Mono game, copy these files from `<GameName>_Data/Managed` into:

`src/U3DViewer.Agent.Mono/lib/`

Required:

- `UnityEngine.CoreModule.dll`
- `UnityEngine.SceneManagementModule.dll`

For older Unity versions that do not use split modules, the project file may need a legacy `UnityEngine.dll` reference instead.

Build:

```powershell
dotnet restore src/U3DViewer.Agent.Mono/U3DViewer.Agent.Mono.csproj
dotnet build src/U3DViewer.Agent.Mono/U3DViewer.Agent.Mono.csproj -c Release
```

When the Viewer is built after this Agent, the Agent DLL is bundled into:

```text
U3DViewer.Viewer/bin/Release/net8.0/payload/Mono/U3DViewer.Agent.Mono.dll
```

The runtime log should contain a PID-specific pipe, for example:

```text
U3D Viewer Mono agent loaded. Pipe: u3d-viewer-12345
Waiting for viewer on pipe 'u3d-viewer-12345'...
```

## 3. IL2CPP setup

Install the matching BepInEx 6 IL2CPP distribution into the game, then launch the game once so BepInEx can generate its interop assemblies.

Copy the generated Unity proxy assemblies from `BepInEx/interop` into:

`src/U3DViewer.Agent.IL2CPP/lib/`

Required:

- `UnityEngine.CoreModule.dll`
- `UnityEngine.SceneManagementModule.dll`

Do not commit the copied game assemblies. The repository `lib/` ignore rule keeps them local.

Build:

```powershell
dotnet restore src/U3DViewer.Agent.IL2CPP/U3DViewer.Agent.IL2CPP.csproj
dotnet build src/U3DViewer.Agent.IL2CPP/U3DViewer.Agent.IL2CPP.csproj -c Release
```

When the Viewer is built after this Agent, the Agent DLL is bundled into:

```text
U3DViewer.Viewer/bin/Release/net8.0/payload/IL2CPP/U3DViewer.Agent.IL2CPP.dll
```

The runtime log should contain a PID-specific pipe, for example:

```text
U3D Viewer IL2CPP agent loaded. Pipe: u3d-viewer-12345
Waiting for viewer on pipe 'u3d-viewer-12345'...
```

## 4. Build and run the standalone Viewer

Build the selected Agent first, then build the Viewer:

```powershell
dotnet build src/U3DViewer.Viewer/U3DViewer.Viewer.csproj -c Release
```

With the repository VSCode workflow, `Ctrl+Shift+B` already performs the correct build order.

Run:

```powershell
.\src\U3DViewer.Viewer\bin\Release\net8.0\U3DViewer.Viewer.exe
```

The first window is a Unity process picker. It lists detected Unity standalone processes with process name/PID, backend, Agent state, and executable path.

### Ready process

Select a process with `Agent = Ready` and click `Attach`. The main Viewer opens against only that process's `u3d-viewer-<PID>` pipe.

### Running process without Agent

If a process is detected as Unity and its Agent is `Not detected`, and the matching bundled payload plus BepInEx are available, the action changes to `Install + Restart`.

The Viewer performs:

```text
copy Agent -> BepInEx/plugins/U3DViewer
copy U3DViewer.Protocol.dll -> BepInEx/plugins/U3DViewer
copy U3DViewer.NativeBridge.dll -> game directory
request graceful game close
relaunch the selected executable
wait for the new u3d-viewer-<PID> pipe
open the main Viewer automatically
```

The Viewer does not force-kill a process that refuses to close. In that case the deployment remains installed and the UI asks you to close/relaunch the game manually.

### Launch a game from the Viewer

Click `Open Game...`, select a Unity `.exe`, and U3DViewer will detect Mono/IL2CPP, deploy the matching bundled Agent, start the executable, wait up to 30 seconds for the PID-specific Agent pipe, and open the main Viewer automatically.

This is launch/install automation, not generic remote DLL injection into an arbitrary running process.

## 5. Main Viewer

The main window contains:

- Runtime Hierarchy
- Runtime Inspector
- Scene View
- Reset / Perspective / Orthographic / Focus Selected controls
- WASD/QE movement and arrow-key look controls when Scene View has focus

When everything is working:

```text
Built Unity game (PID N)
  -> Mono or IL2CPP Agent
  -> u3d-viewer-N control/data pipe
  -> Scene Camera
  -> 1280x720 RenderTexture
  -> NativeBridge writer
  -> named D3D11 shared Texture2D
  -> NativeBridge reader in U3DViewer.exe
  -> staging readback
  -> Avalonia WriteableBitmap
  -> live Scene View
```

The status strip inside Scene View reports bridge problems such as:

- target is not using Direct3D 11
- `U3DViewer.NativeBridge.dll` is missing
- shared texture could not be opened
- native API versions do not match

## Current controls

Click the Scene View first, then use:

```text
W / S      forward / backward
A / D      left / right
Q / E      down / up
Arrow keys look around
```

Use `Focus Selected` after selecting a GameObject in Runtime Hierarchy.

## Current limitations

- GUI automation requires a matching BepInEx 6 runtime to already exist in the game directory.
- `Open Game...` launches the selected executable directly and does not preserve launcher-specific command-line arguments.
- Unity process detection currently targets Windows standalone player layouts and can miss unusual/custom launch layouts.
- Scene target size is fixed at 1280x720.
- Scene image presentation currently uses a GPU-to-CPU staging readback; direct GPU presentation is a later optimization.
- Direct3D 12 and Vulkan are not supported by the Scene transport yet.
- A full recursive hierarchy snapshot is captured once per second; large scenes need incremental updates later.
- Component values are not inspected yet; only component type names are captured.
- IL2CPP component names currently use the managed proxy type exposed by Il2CppInterop.
- `DontDestroyOnLoad`/hidden runtime objects are not specially enumerated yet.
- Picking, collider visualization and transform gizmos are not implemented yet.
- There is no GitHub Actions workflow; build and runtime validation are local/manual.
