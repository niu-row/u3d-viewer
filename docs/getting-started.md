# Getting started

This bootstrap intentionally proves the runtime hierarchy path before adding Avalonia or D3D11 rendering.

## Requirements

- Windows x64
- .NET 8 SDK for the standalone viewer
- A Unity Mono game you are authorized to inspect/debug
- BepInEx 6 installed in that game
- The target game's Unity managed assemblies

## 1. Prepare Unity references

Copy these files from the target game's `<GameName>_Data/Managed` directory into:

`src/U3DViewer.Agent.Mono/lib/`

Required for the current bootstrap:

- `UnityEngine.CoreModule.dll`
- `UnityEngine.SceneManagementModule.dll`

The `lib/` directory is ignored by Git and should remain local because the assemblies come from the target game.

For older Unity versions that do not use split modules, the project file will need a legacy `UnityEngine.dll` reference instead. The first milestone intentionally targets modern modular Unity builds.

## 2. Restore and build

From the repository root:

```powershell
dotnet restore U3DViewer.sln
dotnet build U3DViewer.sln -c Debug
```

BepInEx packages are resolved from the repository `NuGet.Config`.

## 3. Install the agent into the game

Copy the following build outputs into the game's `BepInEx/plugins/U3DViewer/` directory:

- `U3DViewer.Agent.Mono.dll`
- `U3DViewer.Protocol.dll`

Start the game. In the BepInEx log/console you should see:

```text
U3D Viewer Mono agent loaded.
Waiting for viewer on pipe 'u3d-viewer'...
```

## 4. Start the standalone viewer

From the repository root:

```powershell
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

At this stage success is not a 3D window yet. Success means the complete path is working:

```text
built Unity game
  -> BepInEx Mono agent
  -> Unity SceneManager/GameObject APIs on the main thread
  -> snapshot DTO
  -> named pipe
  -> standalone Viewer.exe
```

Only after this path is stable should the project add Avalonia and the Scene Camera/D3D11 transport.

## Known bootstrap limitations

- A full recursive snapshot is captured once per second; large scenes will need incremental updates later.
- Component values are not inspected yet; only component type names are captured.
- `DontDestroyOnLoad`/hidden runtime objects are not specially enumerated yet.
- No object selection or commands are sent back to the game yet.
- No IL2CPP support yet.
- No 3D Scene View yet.
