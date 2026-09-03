# Getting started

This bootstrap proves the runtime hierarchy path before adding Avalonia or D3D11 rendering. Mono and IL2CPP use different game-side agents but emit the same protocol to the same standalone viewer.

## Requirements

- Windows x64
- .NET 8 SDK for the standalone viewer
- .NET 6 SDK for the IL2CPP agent
- A Unity game you are authorized to inspect/debug
- The matching BepInEx 6 runtime for that game

## Mono setup

For a Unity Mono game, copy these files from `<GameName>_Data/Managed` into:

`src/U3DViewer.Agent.Mono/lib/`

Required for the current bootstrap:

- `UnityEngine.CoreModule.dll`
- `UnityEngine.SceneManagementModule.dll`

For older Unity versions that do not use split modules, the project file may need a legacy `UnityEngine.dll` reference instead.

Build:

```powershell
dotnet restore src/U3DViewer.Agent.Mono/U3DViewer.Agent.Mono.csproj
dotnet build src/U3DViewer.Agent.Mono/U3DViewer.Agent.Mono.csproj -c Debug
```

Install these outputs into the game's `BepInEx/plugins/U3DViewer/` directory:

- `U3DViewer.Agent.Mono.dll`
- `U3DViewer.Protocol.dll`

The log should contain:

```text
U3D Viewer Mono agent loaded.
Waiting for viewer on pipe 'u3d-viewer'...
```

## IL2CPP setup

BepInEx 6 IL2CPP currently uses the Unity IL2CPP Bleeding Edge distribution. Install the correct Windows x64 IL2CPP build into the game, then launch the game once so BepInEx can generate its interop assemblies.

BepInEx exposes game/Unity IL2CPP proxy assemblies under `BepInEx/interop`. Copy these generated files into:

`src/U3DViewer.Agent.IL2CPP/lib/`

Required for the current bootstrap:

- `UnityEngine.CoreModule.dll`
- `UnityEngine.SceneManagementModule.dll`

Do not commit the copied game assemblies. The repository `lib/` ignore rule keeps them local.

Build:

```powershell
dotnet restore src/U3DViewer.Agent.IL2CPP/U3DViewer.Agent.IL2CPP.csproj
dotnet build src/U3DViewer.Agent.IL2CPP/U3DViewer.Agent.IL2CPP.csproj -c Debug
```

Install these outputs into the game's `BepInEx/plugins/U3DViewer/` directory:

- `U3DViewer.Agent.IL2CPP.dll`
- `U3DViewer.Protocol.dll`

The log should contain:

```text
U3D Viewer IL2CPP agent loaded.
Waiting for viewer on pipe 'u3d-viewer'...
```

The IL2CPP entry point inherits `BepInEx.Unity.IL2CPP.BasePlugin`. It adds an injected `RuntimeBehaviour` through BepInEx so scene access still runs from Unity's main thread.

## Standalone viewer

The viewer is the same for both backends:

```powershell
dotnet restore src/U3DViewer.Viewer/U3DViewer.Viewer.csproj
dotnet run --project src/U3DViewer.Viewer/U3DViewer.Viewer.csproj
```

Once connected, the console refreshes with the live loaded scenes and GameObject hierarchy.

Example:

```text
U3D Viewer | snapshot #12 | scenes: 1
------------------------------------------------------------------------
▼ Scene: Main  [buildIndex=0, loaded=True]
  ├─ Main Camera  #1024
  ├─ Player  #1108
    ├─ Body  #1110
    ├─ Weapon  #1120
  ├─ Environment  #1200
```

## What success means

At this stage success is not a 3D window yet. Success means either backend can complete this path:

```text
built Unity game
  -> BepInEx Mono or IL2CPP agent
  -> Unity SceneManager/GameObject APIs on the main thread
  -> SceneSnapshot DTO
  -> named pipe
  -> standalone Viewer.exe
```

Only after this path is stable should the project add Avalonia and the Scene Camera/D3D11 transport.

## Known bootstrap limitations

- A full recursive snapshot is captured once per second; large scenes will need incremental updates later.
- Component values are not inspected yet; only component type names are captured.
- IL2CPP component names currently use the managed proxy type exposed by Il2CppInterop; richer runtime type metadata can be added later.
- `DontDestroyOnLoad`/hidden runtime objects are not specially enumerated yet.
- No object selection or commands are sent back to the game yet.
- No 3D Scene View yet.
- There is no GitHub Actions workflow; build and runtime validation are local/manual.
