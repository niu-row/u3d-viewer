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

## 界面语言

Viewer 当前支持 English 和简体中文。

首次运行时会跟随操作系统 UI 语言：`zh-*` 默认选择简体中文，其余语言回退为英文。Viewer 顶部可以随时切换语言，选择会保存到：

```text
%LOCALAPPDATA%/U3DViewer/language.txt
```

诊断日志、构建输出、异常原文、Unity 类型名以及第三方运行时消息会保留原始技术文本，便于直接搜索错误信息。

## 诊断日志

开发版 Viewer 除 Avalonia GUI 外还会保留控制台窗口。准备或连接游戏时建议不要关闭控制台。

以下信息会写入控制台：

- 运行环境准备过程
- 实际解析到的 Unity 引用路径
- compatibility fingerprint
- Agent cache 命中情况
- Agent 构建 stdout/stderr
- Scene GPU / DXGI adapter 信息
- 原生 Scene presenter 初始化或显示错误

同样的日志会保存到：

```text
%LOCALAPPDATA%/U3DViewer/Logs/
  viewer-YYYYMMDD-HHMMSS-<PID>.log
```

Viewer 未处理异常也会写到这里。游戏端 BepInEx / Agent 加载问题继续查看 `BepInEx/LogOutput.log`。

## Agent 复用与缓存

Agent 不会每次启动都重新编译。

缓存位于：

```text
%LOCALAPPDATA%/U3DViewer/AgentCache/
  Mono/<compatibility-fingerprint>/
  IL2CPP/<compatibility-fingerprint>/
```

fingerprint 会包含目标后端、内置 Agent/Protocol Builder 输入，以及 Agent 实际编译所依赖 Unity 程序集的 SHA-256。

因此：

- 同一个游戏再次打开通常直接命中缓存；
- Unity 兼容程序集一致的两个 Mono 游戏可以复用同一 Agent cache；
- 生成出的 Unity proxy assemblies 一致的 IL2CPP 目标也可以复用；
- Unity / interop 更新会自动生成新的 cache key；
- U3DViewer Agent 或 Protocol 源码变化也会自动使旧缓存失效。

这是基于兼容 profile 的复用，而不是把同一个二进制无条件塞给所有 Unity 版本。

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

Viewer 内置的 Agent Builder 只有在目标 compatibility profile 没有缓存时，才会把源码 workspace 复制到 `%LOCALAPPDATA%/U3DViewer/AgentBuilder/`。Mono 使用 BepInEx Unity facade 并结合目标兼容输入；IL2CPP 引用来自 BepInEx 完成生成后的 `BepInEx/interop`。

因此，新 compatibility profile 第一次构建仍需要本机 .NET SDK；命中缓存时不会调用 `dotnet build`。

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
├─ compatibility Agent cache
├─ Lazy Runtime Hierarchy
├─ Runtime Inspector
└─ Scene View
   └─ Avalonia NativeControlHost
      └─ Win32 child HWND
         └─ D3D11 swap chain
            └─ 在与游戏相同的 GPU adapter 上直接采样共享纹理
```

当前 Scene 显示路径不会进行 GPU -> CPU staging readback。Viewer 根据相同的 DXGI adapter LUID 打开命名共享纹理，通过小型 D3D11 shader 直接采样，在 GPU 上完成 Unity RenderTexture 的 Y 翻转，然后直接显示到嵌入式 HWND swap chain。

Hierarchy 使用按需展开策略：首次只加载 Scene roots，只有用户展开的分支才继续读取 children。Unity API 访问仍全部发生在 Unity 主线程，并使用较小的逐帧预算分散扫描成本；snapshot JSON 序列化放在 Unity 主线程之外执行。

## Scene View 操作

Scene host 使用原生 Win32 输入，因此相机导航不依赖键盘自动重复或 Avalonia 指针边界。

```text
右键 + 鼠标      自由观察（Raw Input）
右键 + W/S       前进 / 后退
右键 + A/D       左移 / 右移
右键 + Q/E       下移 / 上移
Shift             临时加速
鼠标滚轮          调整飞行速度
F                 聚焦选中的 GameObject
```

当前飞行速度会直接显示在 Scene 工具栏中，滚轮改变速度后会立即更新数值。

Scene 流参数可以在 Viewer 内直接调整：

```text
Idle FPS        1-120    默认 15
Active FPS      1-120    默认 30
Width           64-4096  默认 1280
Height          64-4096  默认 720
```

修改分辨率时会重新创建 Unity RenderTexture 和 D3D11 shared texture。Idle / Active FPS 用于在 Scene View 响应速度和目标游戏额外 `Camera.Render()` 开销之间取舍。

Scene 镜头参数也可以直接调整，包括 FOV、Near / Far 裁剪面和 Orthographic Size。Perspective / Orthographic 切换由 Viewer 的独立 Scene Camera 管理，不必跟随游戏主相机的投影模式。

## 性能指标

Scene 底部会显示轻量性能指标，用于根据实际测量结果优化：

- Scene `Camera.Render()` CPU 侧提交耗时：最近 / 平均 / 最大；
- Lazy Hierarchy 本轮扫描节点数和扫描耗时：最近 / 平均 / 最大；
- snapshot JSON 序列化耗时；
- snapshot UTF-8 payload 大小。

其中 Scene Render 指标测量的是 `Camera.Render()` 与 native copy event 提交附近的 CPU 耗时，不是 D3D11 GPU timestamp。

## 当前能力

- 发现正在运行的 Unity 进程
- Mono / IL2CPP 检测
- 每进程独立 Agent pipe
- GUI `Attach`、`Prepare + Restart`、`Open Game...`
- 缺失时自动安装 BepInEx 6 x64
- 自动构建并部署与目标兼容的 Agent
- compatibility-keyed Agent cache / 复用
- Lazy 实时 Runtime Hierarchy
- 只读 Runtime Inspector
- 类 Unity Scene View 的飞行相机控制
- 可调 Perspective / Orthographic Scene 镜头
- 可调 Scene FPS 和 RenderTexture 分辨率
- 可见的飞行相机速度
- 运行时性能指标
- D3D11 命名共享纹理 Scene transport
- 游戏端 / Viewer 端 DXGI adapter LUID 诊断
- Viewer Scene GPU 原生显示，无 CPU readback
- GPU 侧 Y flip 和等比例显示
- English / 简体中文 Viewer UI

## 当前限制

- 当前优先支持 Windows x64
- Scene transport 当前要求 Direct3D 11
- 新的、未缓存的 compatibility profile 首次仍需要本机 .NET SDK 编译 Agent
- 特殊/自定义 Unity launcher 可能需要额外进程发现适配
- 游戏仍需要额外执行 Viewer Scene Camera 的渲染；可以通过可调 FPS / 分辨率控制负载
- Hierarchy / Inspector 控制数据目前仍使用 Named Pipe + JSON；Scene 像素始终走 D3D11 shared texture
- 尚未实现 picking、collider 可视化和 transform gizmo
- 不使用 GitHub Actions；当前验证方式为本地手动验证

## 从这里开始

- `docs/getting-started.md`
- `docs/architecture.md`
- `native/U3DViewer.NativeBridge/README.md`
