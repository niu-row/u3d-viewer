using System.Globalization;

namespace U3DViewer.Viewer;

internal sealed class LanguageOption
{
    public LanguageOption(string code, string displayName)
    {
        Code = code;
        DisplayName = displayName;
    }

    public string Code { get; }
    public string DisplayName { get; }
    public override string ToString() => DisplayName;
}

internal static class Localization
{
    private const string English = "en-US";
    private const string ChineseSimplified = "zh-CN";

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "U3DViewer",
        "language.txt");

    public static IReadOnlyList<LanguageOption> Languages { get; } = new[]
    {
        new LanguageOption(English, "English"),
        new LanguageOption(ChineseSimplified, "简体中文")
    };

    public static string CurrentLanguage { get; private set; } = LoadLanguage();
    public static bool IsChinese => CurrentLanguage == ChineseSimplified;

    public static event Action? LanguageChanged;

    public static void SetLanguage(string? language)
    {
        var normalized = Normalize(language);
        if (string.Equals(CurrentLanguage, normalized, StringComparison.Ordinal))
        {
            return;
        }

        CurrentLanguage = normalized;
        SaveLanguage(normalized);
        LanguageChanged?.Invoke();
    }

    public static string T(string key)
    {
        var value = key switch
        {
            "language" => Pair("Language", "语言"),
            "picker.title" => Pair("U3D Viewer - Select Unity Process", "U3D Viewer - 选择 Unity 进程"),
            "picker.heading" => Pair("Select or launch a Unity game", "选择或启动 Unity 游戏"),
            "picker.description" => Pair(
                "Choose a running Unity process or Open Game…. U3DViewer prepares BepInEx, builds or reuses the matching Agent, deploys it, launches/restarts the game, and connects automatically.",
                "选择正在运行的 Unity 进程，或点击“打开游戏…”。U3DViewer 会自动准备 BepInEx、构建或复用匹配的 Agent、完成部署、启动/重启游戏并连接。"),
            "picker.process" => Pair("Process", "进程"),
            "picker.backend" => Pair("Backend", "后端"),
            "picker.agent" => Pair("Agent", "Agent"),
            "picker.path" => Pair("Path", "路径"),
            "picker.select" => Pair("Select a process", "请选择进程"),
            "picker.open" => Pair("Open Game…", "打开游戏…"),
            "picker.refresh" => Pair("Refresh", "刷新"),
            "picker.scanning" => Pair("Scanning running processes...", "正在扫描运行中的 Unity 进程…"),
            "picker.attach" => Pair("Attach", "连接"),
            "picker.busy" => Pair("Agent Busy", "Agent 忙碌"),
            "picker.prepare" => Pair("Prepare + Restart", "准备并重启"),
            "picker.prepareUnavailable" => Pair("Prepare unavailable", "无法自动准备"),
            "picker.agentReady" => Pair("Ready", "就绪"),
            "picker.agentBusy" => Pair("Busy", "忙碌"),
            "picker.agentMissing" => Pair("Not detected", "未检测到"),
            "picker.busyDetail" => Pair("This Agent is already connected to another Viewer.", "该 Agent 已连接到另一个 Viewer。"),
            "picker.openTitle" => Pair("Open Unity game", "打开 Unity 游戏"),
            "picker.exe" => Pair("Windows executable", "Windows 可执行文件"),
            "picker.starting" => Pair("Starting runtime preparation...", "正在准备运行环境…"),
            "picker.readyOpening" => Pair("Agent ready. Opening Viewer...", "Agent 已就绪，正在打开 Viewer…"),
            "picker.timeout" => Pair("Operation timed out or was cancelled.", "操作超时或已取消。"),
            "main.hierarchy" => Pair("Runtime Hierarchy", "运行时层级"),
            "main.scene" => Pair("Scene View", "场景视图"),
            "main.inspector" => Pair("Runtime Inspector", "运行时检查器"),
            "main.resetCamera" => Pair("Reset Camera", "重置相机"),
            "main.perspective" => Pair("Perspective", "透视"),
            "main.orthographic" => Pair("Orthographic", "正交"),
            "main.focusSelected" => Pair("Focus Selected", "聚焦选中"),
            "main.near" => Pair("Near", "近裁剪"),
            "main.far" => Pair("Far", "远裁剪"),
            "main.orthoSize" => Pair("Ortho Size", "正交尺寸"),
            "main.applyLens" => Pair("Apply Lens", "应用镜头"),
            "main.idleFps" => Pair("Idle FPS", "空闲 FPS"),
            "main.activeFps" => Pair("Active FPS", "操作 FPS"),
            "main.width" => Pair("Width", "宽度"),
            "main.height" => Pair("Height", "高度"),
            "main.applyStream" => Pair("Apply Stream", "应用画面"),
            "main.disconnected" => Pair("● Disconnected", "● 未连接"),
            "main.connecting" => Pair("● Connecting", "● 正在连接"),
            "main.connected" => Pair("● Connected", "● 已连接"),
            "main.noSnapshot" => Pair("No snapshot", "暂无快照"),
            "main.waitAgent" => Pair("Waiting for a U3DViewer Agent (Mono or IL2CPP)", "等待 U3DViewer Agent（Mono 或 IL2CPP）"),
            "main.findPipe" => Pair("Looking for the selected process Agent pipe", "正在查找所选进程的 Agent 管道"),
            "main.receiving" => Pair("Receiving lazy hierarchy snapshots and GPU Scene Camera state", "正在接收按需层级快照和 GPU 场景相机状态"),
            "main.waitTarget" => Pair("Waiting for the target game's Scene render target...", "等待目标游戏的场景渲染目标…"),
            "main.perfWaiting" => Pair("Perf · waiting for Agent metrics", "性能 · 等待 Agent 指标"),
            "main.speed" => Pair("Speed {0:0.##} u/s", "速度 {0:0.##} 单位/秒"),
            "main.focusFirst" => Pair("Select a runtime GameObject before using Focus Selected.", "请先在运行时层级中选择 GameObject，再使用“聚焦选中”。"),
            "main.invalidLens" => Pair("Invalid Scene lens values. FOV must be 1-179, Ortho Size > 0, and Far must be greater than Near.", "场景镜头参数无效：FOV 必须为 1-179，正交尺寸必须大于 0，远裁剪面必须大于近裁剪面。"),
            "main.invalidStream" => Pair("Invalid Scene stream values. FPS must be 1-120 and Width/Height must be 64-4096.", "场景画面参数无效：FPS 必须为 1-120，宽高必须为 64-4096。"),
            "main.commandNotSent" => Pair("Camera command not sent: viewer is not connected to an agent.", "相机命令未发送：Viewer 尚未连接 Agent。"),
            "main.selectObject" => Pair("Select a GameObject in Runtime Hierarchy.", "请在运行时层级中选择一个 GameObject。"),
            "main.loading" => Pair("Loading...", "加载中…"),
            "main.sceneLabel" => Pair("Scene: {0}  [build {1}]", "场景：{0}  [build {1}]"),
            "main.inactive" => Pair("{0} (inactive)", "{0}（未激活）"),
            "main.instanceId" => Pair("Instance ID: {0}", "实例 ID：{0}"),
            "main.active" => Pair("Active: {0}  (self: {1})", "激活：{0}  （自身：{1}）"),
            "main.children" => Pair("Children: {0}", "子节点：{0}"),
            "main.layer" => Pair("Layer: {0}", "层：{0}"),
            "main.tag" => Pair("Tag: {0}", "标签：{0}"),
            "main.transform" => Pair("Transform", "变换"),
            "main.position" => Pair("Position:       {0}", "位置：          {0}"),
            "main.localPosition" => Pair("Local Position: {0}", "本地位置：      {0}"),
            "main.euler" => Pair("Euler Angles:   {0}", "欧拉角：        {0}"),
            "main.localScale" => Pair("Local Scale:    {0}", "本地缩放：      {0}"),
            "main.components" => Pair("Components ({0})", "组件（{0}）"),
            "main.none" => Pair("<none>", "<无>"),
            "main.snapshot" => Pair("Snapshot #{0} · {1} scene(s)", "快照 #{0} · {1} 个场景"),
            "main.perf" => Pair(
                "Perf · Render CPU {0:0.00} ms (avg {1:0.00}, max {2:0.00}) · Hierarchy {3} nodes / {4:0.00} ms (avg {5:0.00}, max {6:0.00}) · JSON {7:0.00} ms / {8}",
                "性能 · 渲染 CPU {0:0.00} ms（平均 {1:0.00}，最大 {2:0.00}）· 层级 {3} 节点 / {4:0.00} ms（平均 {5:0.00}，最大 {6:0.00}）· JSON {7:0.00} ms / {8}"),
            "scene.controls" => Pair("RMB + mouse look · RMB + WASD/QE fly · Shift boost · wheel speed · F focus", "右键 + 鼠标观察 · 右键 + WASD/QE 飞行 · Shift 加速 · 滚轮调速 · F 聚焦"),
            "scene.gameGpu" => Pair("Game GPU", "游戏 GPU"),
            "scene.viewerGpu" => Pair("Viewer GPU", "Viewer GPU"),
            "scene.hostFailed" => Pair("Could not create the native D3D11 Scene host window.", "无法创建原生 D3D11 场景宿主窗口。"),
            "scene.presentFailed" => Pair("Scene GPU presentation failed", "场景 GPU 显示失败"),
            "scene.retrying" => Pair("Retrying...", "正在重试…"),
            "scene.presenterMissing" => Pair("U3DViewer.NativeBridge.dll was not found next to U3DViewer.Viewer.exe.", "U3DViewer.Viewer.exe 旁未找到 U3DViewer.NativeBridge.dll。"),
            _ => key
        };

        return value;
    }

    public static string F(string key, params object?[] args) =>
        string.Format(GetCulture(), T(key), args);

    private static string Pair(string english, string chinese) => IsChinese ? chinese : english;

    private static CultureInfo GetCulture() =>
        CultureInfo.GetCultureInfo(IsChinese ? ChineseSimplified : English);

    private static string LoadLanguage()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                return Normalize(File.ReadAllText(SettingsPath).Trim());
            }
        }
        catch
        {
        }

        return CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? ChineseSimplified
            : English;
    }

    private static void SaveLanguage(string language)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, language);
        }
        catch
        {
        }
    }

    private static string Normalize(string? language) =>
        string.Equals(language, ChineseSimplified, StringComparison.OrdinalIgnoreCase) ||
        (!string.IsNullOrWhiteSpace(language) && language.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            ? ChineseSimplified
            : English;
}
