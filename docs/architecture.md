# Architecture

## Goal

Observe the live scene of an already-built Unity game from a standalone `U3DViewer.exe`.

The game-side runtime backend can be Mono or IL2CPP, but both produce the same protocol:

```text
Unity game process
  ├─ U3DViewer.Agent.Mono
  │      or
  ├─ U3DViewer.Agent.IL2CPP
  │    ├─ SceneManager / GameObject / Transform / Component
  │    └─ Scene viewer camera (later milestone)
  │
  ├─ control/data ───── Named Pipe ───────────────┐
  │                                               │
  └─ render target ─── D3D11 shared resource ────┤
                                                  ▼
                                           U3DViewer.exe
                                           ├─ Hierarchy
                                           ├─ Inspector
                                           └─ 3D Scene View
```

## Backend boundary

`U3DViewer.Protocol` must not reference Unity, BepInEx or Il2CppInterop types. Both agents translate runtime objects into plain `SceneSnapshot` DTOs before sending them to the viewer.

Mono uses `BaseUnityPlugin.Update()` directly. IL2CPP uses `BasePlugin.Load()` and attaches an injected `RuntimeBehaviour` with `AddComponent<T>()` so Unity API access still occurs on Unity's main thread.

The standalone viewer therefore does not need separate Mono and IL2CPP implementations.

## Why two transport paths

Scene metadata is small and irregular, so a named pipe is appropriate for scene snapshots, selection, camera commands and inspector requests.

The 3D image is high bandwidth and should not be copied GPU -> CPU -> IPC -> CPU -> GPU each frame. A later Windows/D3D11 milestone will use a native bridge and a shared D3D11 texture/resource.

## Threading rule

Unity object APIs are accessed only on Unity's main thread. Each agent captures plain DTO snapshots from its Unity update callback and hands serialized data to a background pipe thread.

The pipe thread must never call `SceneManager`, `GameObject`, `Transform`, `Component`, `Camera`, `Renderer` or other Unity object APIs.

## Milestones

### M0 — bootstrap

- BepInEx 6 Mono agent loads in a built Mono game.
- BepInEx 6 IL2CPP agent loads in a built IL2CPP game.
- Standalone viewer process starts.

### M1 — runtime hierarchy

- Enumerate all loaded scenes.
- Recursively capture root GameObjects and children.
- Capture instance ID, active state, layer, tag, Transform and component type names.
- Stream the same snapshot format from either backend to the standalone viewer.

### M2 — desktop UI

- Replace console output with Avalonia.
- Hierarchy panel.
- Inspector panel.
- Connection/status UI.

### M3 — scene camera control

- Create an isolated runtime Camera in each backend.
- Viewer sends WASD/mouse-look/focus commands.
- Perspective/orthographic controls.

### M4 — D3D11 scene transport

- Native Unity graphics bridge.
- Copy Scene Camera RenderTexture into a shareable D3D11 Texture2D.
- Open shared resource from `U3DViewer.exe`.
- Display it in the Scene panel.

### M5 — scene tools

- Object picking.
- Renderer bounds.
- Collider visualization.
- Camera frustums.
- Grid and transform gizmos.

### M6 — runtime hardening

- Incremental hierarchy updates instead of full snapshots.
- Better IL2CPP runtime component type resolution.
- `DontDestroyOnLoad` and hidden object enumeration.
- Compatibility testing across Unity versions.

## Current constraints

The bootstrap targets Windows x64, Unity Mono and Unity IL2CPP. It is read-only and intended for games the operator is authorized to inspect/debug. DX12 and Vulkan are deferred until the D3D11 runtime pipeline is stable.
