# u3d-viewer

[English](README.md) | [简体中文](README.zh-CN.md)

面向已经打包完成的 Unity 游戏的独立运行时查看与检查工具。

## 使用流程

正常流程以 GUI 为主，不需要预先编写 `gamePath` 或 `backend` 配置文件。

```text
Ctrl+Shift+B
  -> 构建 Viewer + NativeBridge + 内置 Agent Builder 源码

U3DViewer.Viewer.exe
  -> 选择正在运行的 Unity 进程
       Ready              -> Attach
       Agent not detected -> Prepare + Restart
  或 Open Game...
       -> 选择 Game.exe

随后 U3DViewer 会自动：
  -> 检测 Mono / IL2CPP
  -> 缺失时安装固定版本的 BepInEx 6 x64
  -> IL2CPP 目标需要生成 interop 时，自动启动一次游戏完成生成
  -> 对兼容的 Unity/interop API 表面生成 fingerprint
  -> 已存在对应兼容缓存时直接复用 Agent
  -> 否则只构建一次 Agent 并缓存
  -> 部署 Agent + Protocol + NativeBridge
  -> 启动/重启游戏
  -> 等待 u3d-viewer-<PID>
  -> 打开 Hierarchy / Inspector / Scene View
```

通过 `Prepare + Restart` 选择已经运行的游戏时，U3DViewer 只会请求其正常退出，不会强制杀掉用户正在玩的进程。由 U3DViewer 自己启动、仅用于 IL2CPP interop 生成的临时 bootstrap 进程，在生成完成后可以由 U3DViewer 终止。

## 运行数据目录

Viewer 自身的全局数据统一放在 `U3DViewer.Viewer.exe` 旁边：

```text
<Viewer>\U3DViewer\
├─ language.txt
└─ Logs\
```

每个游戏自己的 U3DViewer 数据统一放在游戏 exe 旁边：

```text
<Game>\U3DViewer\
├─ Settings\scene.json
├─ Downloads\
├─ AgentCache\<Backend>\<compatibility-fingerprint>\
├─ Temp\AgentBuilder\<Backend>-<ViewerPID>\
└─ Logs\viewer-*.log
```

选中游戏后，当前 Viewer 日志会转移到这个游戏的 `U3DViewer/Logs`。Agent 构建成功后会复制到 `AgentCache`，对应的临时 workspace 会删除；构建失败时临时 workspace 会保留下来方便排查。

旧版本使用 `%LOCALAPPDATA%\U3DViewer`。新版不再向该位置写入运行数据，但不会自动删除历史文件。

## 界面语言

Viewer 当前支持 English 和简体中文。首次运行时会跟随操作系统 UI 语言：`zh-*` 默认选择简体中文，其余语言回退为英文。语言设置保存在：

```text
<Viewer>\U3DViewer\language.txt
```

诊断日志、构建输出、异常原文、Unity 类型名以及第三方运行时消息会保留原始技术文本，便于直接搜索错误信息。

## 诊断日志

开发版 Viewer 除 Avalonia GUI 外还会保留控制台窗口。准备或连接游戏时建议不要关闭控制台。

以下信息会写入控制台：运行环境准备过程、Unity 引用路径、compatibility fingerprint、Agent cache 命中情况、Agent 构建输出、Scene GPU / DXGI adapter 信息以及原生 Scene presenter 错误。

未选择游戏前，文件日志位于 `<Viewer>/U3DViewer/Logs`；选择游戏后会转移到：

```text
<Game>\U3DViewer\Logs\viewer-YYYYMMDD-HHMMSS-<PID>.log
```

游戏端 BepInEx / Agent 加载问题继续查看 `BepInEx/LogOutput.log`。

## Agent 复用与缓存

Agent 不会每次启动都重新编译。每个游戏自己的缓存位于：

```text
<Game>\U3DViewer\AgentCache\
  Mono/<compatibility-fingerprint>/
  IL2CPP/<compatibility-fingerprint>/
```

fingerprint 会包含目标后端、内置 Agent/Protocol Builder 输入，以及 Agent 实际编译所依赖 Unity 程序集的 SHA-256。因此同一个游戏再次打开通常会直接命中自己的缓存；Unity / interop 更新，或 U3DViewer Agent / Protocol 源码变化时，会自动生成新的 cache key。

## 在 VSCode 中构建

开发环境要求：

- Windows x64
- .NET 8 SDK
- Visual Studio 2022 C++ workload、Windows SDK 和 CMake

按下 `Ctrl+Shift+B`。默认任务执行 `scripts/build.ps1`，输出：

```text
build/native/Release/U3DViewer.NativeBridge.dll
src/U3DViewer.Viewer/bin/Release/net8.0/U3DViewer.Viewer.exe
src/U3DViewer.Viewer/bin/Release/net8.0/agent-builder/...
```

构建 Viewer 本身时不需要指定目标游戏路径。

Viewer 内置的 Agent Builder 只有在目标游戏对应 compatibility profile 没有缓存时，才会把源码 workspace 复制到：

```text
<Game>\U3DViewer\Temp\AgentBuilder\
```

Mono 使用 BepInEx Unity facade 并结合目标兼容输入；IL2CPP 引用来自 BepInEx 完成生成后的 `BepInEx/interop`。新的 compatibility profile 第一次构建仍需要本机 .NET SDK；命中缓存时不会调用 `dotnet build`。

## 运行时架构

```text
Unity Game.exe
├─ BepInEx 6
├─ U3DViewer.Agent.Mono.dll OR U3DViewer.Agent.IL2CPP.dll
├─ U3DViewer.Protocol.dll
└─ U3DViewer.NativeBridge.dll
        │
        ├─ Named Pipe: u3d-viewer-<PID>
        └─ D3D11 named shared Texture2D + keyed mutex
                ▼
U3DViewer.Viewer.exe
├─ 进程选择 / Open Game
├─ 自动运行环境准备
├─ 每游戏独立 compatibility Agent cache
├─ Lazy Runtime Hierarchy
├─ Runtime Inspector
└─ Scene View
   └─ Avalonia NativeControlHost
      └─ Win32 child HWND
         └─ 独立 presenter 线程
            └─ D3D11 swap chain
               └─ 在与游戏相同的 GPU adapter 上直接采样共享纹理
```

当前 Scene 显示路径不会进行 GPU -> CPU staging readback。Viewer 根据相同的 DXGI adapter LUID 打开命名共享纹理，通过 D3D11 shader 直接采样，在 GPU 上完成 Unity RenderTexture 的 Y 翻转，然后直接显示到嵌入式 HWND swap chain。

Presenter 的 Open / Close / Present 全部串行运行在一条专用 Viewer 线程上。交互式拖动窗口尺寸时会暂停 presenter，尺寸稳定后按最终大小重新创建；`Present` 热路径不调用 `ResizeBuffers`。拖动过程中 DXGI 可以暂时把旧 backbuffer 拉伸到当前 HWND，停止调整后再以最终尺寸打开新的 swap chain，避免 resize 时序和 DXGI 相互竞争。

Agent 会回报游戏进程实际加载的 NativeBridge ABI 版本。若它和 Viewer/Protocol 期望的 ABI 不一致，Viewer 会直接拒绝 Scene presenter，并明确提示重新部署与重启游戏，而不是继续进入难以判断的 DXGI 错误。

Hierarchy 使用按需展开策略：首次只加载 Scene roots，只有用户展开的分支才继续读取 children。Unity API 访问仍全部发生在 Unity 主线程，并使用较小的逐帧预算分散扫描成本；snapshot JSON 序列化放在 Unity 主线程之外执行。

## Scene View 操作

```text
右键 + 鼠标      自由观察（Raw Input）
右键 + W/S       前进 / 后退
右键 + A/D       左移 / 右移
右键 + Q/E       下移 / 上移
Shift             临时加速
鼠标滚轮          调整飞行速度
F                 聚焦选中的 GameObject
```

当前飞行速度会直接显示在 Scene 工具栏中，滚轮改变速度后会立即更新数值。透视 / 正交是互斥状态控件，会跟随 Agent 返回的实际投影模式高亮。

Scene 工具栏还提供两个独立开关：`跟随位置` 和 `跟随朝向`。开启后，Scene Camera 在真正渲染一帧之前，只同步所勾选的 `Camera.main` Transform 分量；两个都开启就是完整跟随主相机 Transform，但 FOV、投影、Near/Far 和 Culling 仍由 U3DViewer 自己控制。这两个开关按游戏持久化。

Agent 有可能在游戏 `Camera.main` 尚未准备好时创建 Scene Camera。现在 Viewer 在 pipe 连接成功后立即开始一个短暂 bootstrap：先发送一次 Reset Camera，若 Scene target 还未可用则每 500 ms 重试一次，最多约 10 秒；第一个可用 Scene target 出现后立即停止，因此不会在正常观察过程中反复重置视角。

Scene 主工具栏只保留高频操作。FOV、Near/Far、Orthographic Size、Idle/Active FPS、分辨率方式和 Culling Mask 都放在独立的“场景设置”窗口里。窄窗口下工具栏会自动换行，不再强制挤成一整条横排。

默认流设置为：

```text
Idle FPS        15
Active FPS      30
自动匹配视口     开启
手动尺寸回退     1280 x 720
```

自动匹配视口开启时，Unity RenderTexture 会跟随 Scene View 实际宽度、高度和比例。拖动窗口时会做防抖，停止调整后才重建共享纹理。Auto Viewport 只响应真实 `SizeChanged`，并带有少量像素容差；底部状态区固定高度且单行截断，因此错误文本不会再改变 Scene Host 高度并形成 resize 反馈环。关闭自动匹配后可以使用 64-4096 的固定宽高。

场景设置会按游戏持久化到：

```text
<Game>\U3DViewer\Settings\scene.json
```

保存内容包括 FOV、Near/Far、Orthographic Size、Idle/Active FPS、自动/手动分辨率设置、Culling 模式和 Mask，以及两个主相机 Transform 跟随开关。再次连接同一个游戏时会自动恢复。

修改 Scene 分辨率时会重新创建 Unity RenderTexture 和 D3D11 shared texture。Agent 在 `camera.stream` 修改后会立即刷新一次 Scene target 状态，因此 Viewer 不需要再等待正常约 1 秒的 snapshot 周期才能打开新的共享纹理。

如果 presenter 丢失当前 shared-resource generation，watchdog 最多每 1 秒请求一次 `camera.recover`。恢复动作只重建 Scene RenderTexture / shared transport generation 并安排新的 render/copy，不会修改当前 Scene Camera 的位置、朝向、FOV、投影、裁剪面或 Culling。

Viewer 最小化或 Scene 面板不可见时，会发送 `camera.visibility=0`，Agent 停止额外的 Scene `Camera.Render()`，但保留最后一帧以及正常的 Hierarchy / Inspector 连接。重新可见时发送 `camera.visibility=1`，并立即恢复 Scene 渲染。

Scene Camera 的 `cullingMask` 可以独立选择：`所有` 渲染全部 32 个 Unity Layer；`复制主相机` 按 snapshot 节奏跟随当前 `Camera.main.cullingMask`；`手动` 打开 32 Layer 勾选窗口，并显示游戏实际定义的 Layer 名称。没有游戏级保存配置时，默认仍然是 `复制主相机`。

Hierarchy 和 Inspector 两侧栏可以通过 splitter 拖动宽度，中间 Scene View 自动占用剩余区域。

## 性能指标

Scene 底部显示精简的当前性能指标：

- 游戏实际 FPS（短时间滚动窗口）；
- U3DViewer Scene Camera 实际渲染 FPS；
- Scene `Camera.Render()` CPU 侧提交耗时；
- Lazy Hierarchy 本轮扫描节点数和扫描耗时；
- snapshot JSON 序列化耗时；
- snapshot UTF-8 payload 大小。

Agent 内部仍保留平均值和最大值统计。Scene Render 指标测量的是 `Camera.Render()` 与 native copy event 提交附近的 CPU 耗时，不是 D3D11 GPU timestamp。

## 当前能力

- 发现正在运行的 Unity 进程
- Mono / IL2CPP 检测
- 每进程独立 Agent pipe
- GUI `Attach`、`Prepare + Restart`、`Open Game...`
- 缺失时自动安装 BepInEx 6 x64
- 自动构建并部署与目标兼容的 Agent
- 每游戏独立 compatibility-keyed Agent cache
- 每游戏持久化 Scene 设置
- Lazy 实时 Runtime Hierarchy
- 只读 Runtime Inspector
- 可拖动的 Hierarchy / Scene / Inspector 工作区
- 类 Unity Scene View 的飞行相机控制
- 可调 Perspective / Orthographic Scene 镜头，并显示当前投影状态
- 连接后的主相机姿态 bootstrap
- 独立的主相机位置 / 朝向跟随开关
- 自动自由比例 Scene RenderTexture 尺寸或固定手动分辨率
- 可调 Scene FPS
- 可选 Scene Camera culling mask：所有 / 复制主相机 / 手动 Layer
- 可见的飞行相机速度
- 游戏 FPS / Scene FPS / 运行时性能指标
- D3D11 命名共享纹理 Scene transport
- Viewer Scene GPU 原生显示，无 CPU readback
- 独立 Scene presenter 线程与 resize 恢复
- 不重置相机姿态的 transport-only watchdog 恢复
- NativeBridge ABI / 版本不匹配检测
- Viewer / Scene 不可见时自动暂停额外 Scene render
- GPU 侧 Y flip 和等比例显示
- English / 简体中文 Viewer UI

## 当前限制

- 当前优先支持 Windows x64
- Scene transport 当前要求 Direct3D 11
- 新的、未缓存的 compatibility profile 首次仍需要本机 .NET SDK 编译 Agent
- 特殊/自定义 Unity launcher 可能需要额外进程发现适配
- Scene View 可见时，游戏仍需要额外执行 Viewer Scene Camera 的渲染；可以通过 Scene FPS / 分辨率设置控制负载
- Hierarchy / Inspector 控制数据目前仍使用 Named Pipe + JSON；Scene 像素始终走 D3D11 shared texture
- 尚未实现 picking、collider 可视化和 transform gizmo
- 不使用 GitHub Actions；当前验证方式为本地手动验证

## 从这里开始

- `docs/getting-started.md`
- `docs/architecture.md`
- `native/U3DViewer.NativeBridge/README.md`