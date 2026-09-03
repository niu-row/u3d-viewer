using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

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

    internal const string SkipAutoTranslateClass = "u3d-localization-skip";

    private static readonly ConditionalWeakTable<Window, object> AttachedWindows = new();
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "U3DViewer",
        "language.txt");

    private static readonly IReadOnlyDictionary<string, (string En, string Zh)> Text =
        new Dictionary<string, (string En, string Zh)>(StringComparer.Ordinal)
        {
            ["language"] = ("Language", "语言"),
            ["picker.title"] = ("U3D Viewer - Select Unity Process", "U3D Viewer - 选择 Unity 进程"),
            ["picker.heading"] = ("Select or launch a Unity game", "选择或启动 Unity 游戏"),
            ["picker.description"] = (
                "Choose a running Unity process or Open Game…. U3DViewer prepares BepInEx, builds or reuses the matching Agent, deploys it, launches/restarts the game, and connects automatically.",
                "选择正在运行的 Unity 进程，或点击“打开游戏…”。U3DViewer 会自动准备 BepInEx、构建或复用匹配的 Agent、完成部署、启动/重启游戏并连接。"),
            ["picker.process"] = ("Process", "进程"),
            ["picker.backend"] = ("Backend", "后端"),
            ["picker.agent"] = ("Agent", "Agent"),
            ["picker.path"] = ("Path", "路径"),
            ["picker.select"] = ("Select a process", "请选择进程"),
            ["picker.open"] = ("Open Game…", "打开游戏…"),
            ["picker.refresh"] = ("Refresh", "刷新"),
            ["picker.scanning"] = ("Scanning running processes...", "正在扫描运行中的 Unity 进程…"),
            ["picker.attach"] = ("Attach", "连接"),
            ["picker.busy"] = ("Agent Busy", "Agent 忙碌"),
            ["picker.prepare"] = ("Prepare + Restart", "准备并重启"),
            ["picker.prepareUnavailable"] = ("Prepare unavailable", "无法自动准备"),
            ["picker.ready"] = ("Ready", "就绪"),
            ["picker.busyStatus"] = ("Busy", "忙碌"),
            ["picker.notDetected"] = ("Not detected", "未检测到"),
            ["picker.busyDetail"] = ("This Agent is already connected to another Viewer.", "该 Agent 已连接到另一个 Viewer。"),
            ["picker.openTitle"] = ("Open Unity game", "打开 Unity 游戏"),
            ["picker.exe"] = ("Windows executable", "Windows 可执行文件"),
            ["picker.starting"] = ("Starting runtime preparation...", "正在准备运行环境…"),
            ["picker.readyOpening"] = ("Agent ready. Opening Viewer...", "Agent 已就绪，正在打开 Viewer…"),
            ["picker.timeout"] = ("Operation timed out or was cancelled.", "操作超时或已取消。"),
            ["main.hierarchy"] = ("Runtime Hierarchy", "运行时层级"),
            ["main.scene"] = ("Scene View", "场景视图"),
            ["main.inspector"] = ("Runtime Inspector", "运行时检查器"),
            ["main.resetCamera"] = ("Reset Camera", "重置相机"),
            ["main.perspective"] = ("Perspective", "透视"),
            ["main.orthographic"] = ("Orthographic", "正交"),
            ["main.focusSelected"] = ("Focus Selected", "聚焦选中"),
            ["main.near"] = ("Near", "近裁剪"),
            ["main.far"] = ("Far", "远裁剪"),
            ["main.orthoSize"] = ("Ortho Size", "正交尺寸"),
            ["main.applyLens"] = ("Apply Lens", "应用镜头"),
            ["main.idleFps"] = ("Idle FPS", "空闲 FPS"),
            ["main.activeFps"] = ("Active FPS", "操作 FPS"),
            ["main.width"] = ("Width", "宽度"),
            ["main.height"] = ("Height", "高度"),
            ["main.applyStream"] = ("Apply Stream", "应用画面"),
            ["main.disconnected"] = ("● Disconnected", "● 未连接"),
            ["main.connecting"] = ("● Connecting", "● 正在连接"),
            ["main.connected"] = ("● Connected", "● 已连接"),
            ["main.noSnapshot"] = ("No snapshot", "暂无快照"),
            ["main.waitAgent"] = ("Waiting for a U3DViewer Agent (Mono or IL2CPP)", "等待 U3DViewer Agent（Mono 或 IL2CPP）"),
            ["main.findPipe"] = ("Looking for the selected process Agent pipe", "正在查找所选进程的 Agent 管道"),
            ["main.receiving"] = ("Receiving lazy hierarchy snapshots and GPU Scene Camera state", "正在接收按需层级快照和 GPU 场景相机状态"),
            ["main.waitTarget"] = ("Waiting for the target game's Scene render target...", "等待目标游戏的场景渲染目标…"),
            ["main.perfWaiting"] = ("Perf · waiting for Agent metrics", "性能 · 等待 Agent 指标"),
            ["main.focusFirst"] = ("Select a runtime GameObject before using Focus Selected.", "请先在运行时层级中选择 GameObject，再使用“聚焦选中”。"),
            ["main.invalidLens"] = ("Invalid Scene lens values. FOV must be 1-179, Ortho Size > 0, and Far must be greater than Near.", "场景镜头参数无效：FOV 必须为 1-179，正交尺寸必须大于 0，远裁剪面必须大于近裁剪面。"),
            ["main.invalidStream"] = ("Invalid Scene stream values. FPS must be 1-120 and Width/Height must be 64-4096.", "场景画面参数无效：FPS 必须为 1-120，宽高必须为 64-4096。"),
            ["main.commandNotSent"] = ("Camera command not sent: viewer is not connected to an agent.", "相机命令未发送：Viewer 尚未连接 Agent。"),
            ["main.selectObject"] = ("Select a GameObject in Runtime Hierarchy.", "请在运行时层级中选择一个 GameObject。"),
            ["main.loading"] = ("Loading...", "加载中…"),
            ["main.transform"] = ("Transform", "变换"),
            ["main.none"] = ("<none>", "<无>"),
            ["main.cullingMask"] = ("Culling Mask", "剔除遮罩"),
            ["main.layers"] = ("Layers…", "图层…"),
            ["main.mask"] = ("Mask", "遮罩"),
            ["main.cullingAll"] = ("All", "所有"),
            ["main.cullingMainCamera"] = ("Copy Main Camera", "复制主相机"),
            ["main.cullingManual"] = ("Manual", "手动"),
            ["main.manualCullingTitle"] = ("Manual Culling Mask", "手动剔除遮罩"),
            ["main.everything"] = ("Everything", "全部"),
            ["main.nothing"] = ("Nothing", "全不选"),
            ["main.cancel"] = ("Cancel", "取消"),
            ["main.applyManualMask"] = ("Apply Manual Mask", "应用手动遮罩"),
            ["main.cullingDescription"] = (
                "Choose the Unity Layers rendered by the Scene Camera.",
                "选择场景相机要渲染的 Unity Layer。"),
            ["scene.hostFailed"] = ("Could not create the native D3D11 Scene host window.", "无法创建原生 D3D11 场景宿主窗口。"),
            ["scene.bridgeMissing"] = ("U3DViewer.NativeBridge.dll was not found next to U3DViewer.Viewer.exe.", "U3DViewer.Viewer.exe 旁未找到 U3DViewer.NativeBridge.dll。")
        };

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

    public static string T(string key) =>
        Text.TryGetValue(key, out var pair)
            ? IsChinese ? pair.Zh : pair.En
            : key;

    public static string Translate(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        foreach (var pair in Text.Values)
        {
            if (string.Equals(input, pair.En, StringComparison.Ordinal) ||
                string.Equals(input, pair.Zh, StringComparison.Ordinal))
            {
                return IsChinese ? pair.Zh : pair.En;
            }
        }

        return IsChinese ? TranslateDynamicToChinese(input) : TranslateDynamicToEnglish(input);
    }

    public static void Attach(Window window)
    {
        if (window.Content is not Control originalContent || AttachedWindows.TryGetValue(window, out _))
        {
            return;
        }
        AttachedWindows.Add(window, new object());

        var selector = new ComboBox
        {
            ItemsSource = Languages,
            SelectedItem = Languages.First(item => item.Code == CurrentLanguage),
            MinWidth = 110,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        var label = new TextBlock
        {
            Text = T("language"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(10, 5),
            Children = { label, selector }
        };

        var wrapper = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        wrapper.Children.Add(new Border
        {
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = bar
        });
        Grid.SetRow(originalContent, 1);
        wrapper.Children.Add(originalContent);
        window.Content = wrapper;

        var updatingSelector = false;
        selector.SelectionChanged += (_, _) =>
        {
            if (!updatingSelector && selector.SelectedItem is LanguageOption option)
            {
                SetLanguage(option.Code);
            }
        };

        void RefreshLanguage()
        {
            label.Text = T("language");
            updatingSelector = true;
            selector.SelectedItem = Languages.First(item => item.Code == CurrentLanguage);
            updatingSelector = false;
            Apply(window);
        }

        LanguageChanged += RefreshLanguage;
        window.Closed += (_, _) => LanguageChanged -= RefreshLanguage;
        RefreshLanguage();
    }

    private static void Apply(Window window)
    {
        window.Title = Translate(window.Title ?? string.Empty);

        foreach (var block in window.GetVisualDescendants().OfType<TextBlock>())
        {
            if (block.Classes.Contains(SkipAutoTranslateClass) || string.IsNullOrEmpty(block.Text))
            {
                continue;
            }

            block.Text = Translate(block.Text);
        }

        foreach (var button in window.GetVisualDescendants().OfType<Button>())
        {
            if (button.Content is string text)
            {
                button.Content = Translate(text);
            }
        }
    }

    private static string TranslateDynamicToChinese(string input)
    {
        var result = Regex.Replace(input, @"^Found (\d+) Unity process\(es\) · (\d+) ready$", "发现 $1 个 Unity 进程 · $2 个已就绪");
        if (result != input) return result;
        result = Regex.Replace(input, @"^(.+) · PID (\d+) · Agent ready$", "$1 · PID $2 · Agent 已就绪");
        if (result != input) return result;
        result = Regex.Replace(input, @"^(.+) detected\. U3DViewer can prepare the runtime and restart this game automatically\.$", "已检测到 $1。U3DViewer 可以自动准备运行环境并重启游戏。");
        if (result != input) return result;
        result = Regex.Replace(input, @"^Process scan failed: (.+)$", "进程扫描失败：$1");
        if (result != input) return result;
        result = Regex.Replace(input, @"^Operation failed: (.+)$", "操作失败：$1");
        if (result != input) return result;
        result = Regex.Replace(input, @"^Snapshot #(\d+) · (\d+) scene\(s\)$", "快照 #$1 · $2 个场景");
        if (result != input) return result;
        result = Regex.Replace(input, @"^Scene: (.+)  \[build (-?\d+)\]$", "场景：$1  [build $2]");
        if (result != input) return result;
        result = Regex.Replace(input, @"^(.+) \(inactive\)$", "$1（未激活）");
        if (result != input) return result;
        result = Regex.Replace(input, @"^Speed ([0-9.]+) u/s$", "速度 $1 单位/秒");
        if (result != input) return result;

        result = ReplacePrefix(input, "Instance ID: ", "实例 ID：");
        result = ReplacePrefix(result, "Active: ", "激活：");
        result = result.Replace("  (self: ", "  （自身：", StringComparison.Ordinal).Replace(")", "）", StringComparison.Ordinal);
        result = ReplacePrefix(result, "Children: ", "子节点：");
        result = ReplacePrefix(result, "Layer: ", "层：");
        result = ReplacePrefix(result, "Tag: ", "标签：");
        result = ReplacePrefix(result, "Position:       ", "位置：          ");
        result = ReplacePrefix(result, "Local Position: ", "本地位置：      ");
        result = ReplacePrefix(result, "Euler Angles:   ", "欧拉角：        ");
        result = ReplacePrefix(result, "Local Scale:    ", "本地缩放：      ");
        result = Regex.Replace(result, @"^Components \((\d+)\)$", "组件（$1）");

        return result
            .Replace("Perf · Render CPU ", "性能 · 渲染 CPU ", StringComparison.Ordinal)
            .Replace("Perf · Render ", "性能 · 渲染 ", StringComparison.Ordinal)
            .Replace("(avg ", "（平均 ", StringComparison.Ordinal)
            .Replace(", max ", "，最大 ", StringComparison.Ordinal)
            .Replace(") · Hierarchy ", "）· 层级 ", StringComparison.Ordinal)
            .Replace(" nodes / ", " 节点 / ", StringComparison.Ordinal)
            .Replace(") · JSON ", "）· JSON ", StringComparison.Ordinal)
            .Replace("RMB + mouse look · RMB + WASD/QE fly · Shift boost · wheel speed · F focus", "右键 + 鼠标观察 · 右键 + WASD/QE 飞行 · Shift 加速 · 滚轮调速 · F 聚焦", StringComparison.Ordinal)
            .Replace("Game GPU:", "游戏 GPU：", StringComparison.Ordinal)
            .Replace("Viewer GPU:", "Viewer GPU：", StringComparison.Ordinal);
    }

    private static string TranslateDynamicToEnglish(string input)
    {
        var result = Regex.Replace(input, @"^发现 (\d+) 个 Unity 进程 · (\d+) 个已就绪$", "Found $1 Unity process(es) · $2 ready");
        if (result != input) return result;
        result = Regex.Replace(input, @"^(.+) · PID (\d+) · Agent 已就绪$", "$1 · PID $2 · Agent ready");
        if (result != input) return result;
        result = Regex.Replace(input, @"^已检测到 (.+)。U3DViewer 可以自动准备运行环境并重启游戏。$", "$1 detected. U3DViewer can prepare the runtime and restart this game automatically.");
        if (result != input) return result;
        result = Regex.Replace(input, @"^进程扫描失败：(.+)$", "Process scan failed: $1");
        if (result != input) return result;
        result = Regex.Replace(input, @"^操作失败：(.+)$", "Operation failed: $1");
        if (result != input) return result;
        result = Regex.Replace(input, @"^快照 #(\d+) · (\d+) 个场景$", "Snapshot #$1 · $2 scene(s)");
        if (result != input) return result;
        result = Regex.Replace(input, @"^场景：(.+)  \[build (-?\d+)\]$", "Scene: $1  [build $2]");
        if (result != input) return result;
        result = Regex.Replace(input, @"^(.+)（未激活）$", "$1 (inactive)");
        if (result != input) return result;
        result = Regex.Replace(input, @"^速度 ([0-9.]+) 单位/秒$", "Speed $1 u/s");
        if (result != input) return result;

        result = ReplacePrefix(input, "实例 ID：", "Instance ID: ");
        result = ReplacePrefix(result, "激活：", "Active: ");
        result = result.Replace("  （自身：", "  (self: ", StringComparison.Ordinal).Replace("）", ")", StringComparison.Ordinal);
        result = ReplacePrefix(result, "子节点：", "Children: ");
        result = ReplacePrefix(result, "层：", "Layer: ");
        result = ReplacePrefix(result, "标签：", "Tag: ");
        result = ReplacePrefix(result, "位置：          ", "Position:       ");
        result = ReplacePrefix(result, "本地位置：      ", "Local Position: ");
        result = ReplacePrefix(result, "欧拉角：        ", "Euler Angles:   ");
        result = ReplacePrefix(result, "本地缩放：      ", "Local Scale:    ");
        result = Regex.Replace(result, @"^组件（(\d+)）$", "Components ($1)");

        return result
            .Replace("性能 · 渲染 CPU ", "Perf · Render CPU ", StringComparison.Ordinal)
            .Replace("性能 · 渲染 ", "Perf · Render ", StringComparison.Ordinal)
            .Replace("（平均 ", "(avg ", StringComparison.Ordinal)
            .Replace("，最大 ", ", max ", StringComparison.Ordinal)
            .Replace("）· 层级 ", ") · Hierarchy ", StringComparison.Ordinal)
            .Replace(" 节点 / ", " nodes / ", StringComparison.Ordinal)
            .Replace("）· JSON ", ") · JSON ", StringComparison.Ordinal)
            .Replace("右键 + 鼠标观察 · 右键 + WASD/QE 飞行 · Shift 加速 · 滚轮调速 · F 聚焦", "RMB + mouse look · RMB + WASD/QE fly · Shift boost · wheel speed · F focus", StringComparison.Ordinal)
            .Replace("游戏 GPU：", "Game GPU:", StringComparison.Ordinal)
            .Replace("Viewer GPU：", "Viewer GPU:", StringComparison.Ordinal);
    }

    private static string ReplacePrefix(string input, string from, string to) =>
        input.StartsWith(from, StringComparison.Ordinal) ? to + input[from.Length..] : input;

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
