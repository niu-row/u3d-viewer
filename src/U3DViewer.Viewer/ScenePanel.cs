using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using U3DViewer.Protocol;

namespace U3DViewer.Viewer;

internal sealed class ScenePanel : Grid
{
    private const int MinRenderSize = 64;
    private const int MaxRenderSize = 4096;

    private readonly Window _owner;
    private readonly Action<string> _sendCommand;
    private readonly Func<int?> _selectedInstanceId;
    private readonly Action<string> _setDetail;
    private readonly NativeSceneHost _sceneHost;
    private readonly TextBlock _sceneStatus;
    private readonly TextBlock _performanceStatus;
    private readonly TextBlock _moveSpeedStatus;
    private readonly Button _settingsButton;
    private readonly DispatcherTimer _resizeDebounce = new();
    private readonly string _gameExecutablePath;

    private RenderTargetInfo? _latestTarget;
    private SceneSettingsProfile? _savedProfile;
    private bool _savedProfileApplied;
    private bool _autoViewport = true;
    private Size _pendingViewportSize;
    private int _requestedAutoWidth;
    private int _requestedAutoHeight;

    public ScenePanel(
        Window owner,
        Action<string> sendCommand,
        Func<int?> selectedInstanceId,
        Action<string> setDetail)
    {
        _owner = owner;
        _sendCommand = sendCommand;
        _selectedInstanceId = selectedInstanceId;
        _setDetail = setDetail;
        _gameExecutablePath = ViewerSession.Target?.ExecutablePath ?? string.Empty;
        _savedProfile = string.IsNullOrWhiteSpace(_gameExecutablePath)
            ? null
            : SceneSettingsStore.Load(_gameExecutablePath);
        _autoViewport = _savedProfile?.AutoViewport ?? true;

        RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto");

        Children.Add(new TextBlock
        {
            Text = Localization.T("main.scene"),
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(10, 10, 10, 8)
        });

        _moveSpeedStatus = new TextBlock
        {
            Text = Localization.Translate("Speed 10 u/s"),
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeight.SemiBold
        };

        _settingsButton = CreateCommandButton(SettingsLabel(), ShowSettings);

        var commands = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6
        };
        commands.Children.Add(CreateCommandButton(
            Localization.T("main.resetCamera"),
            () => _sendCommand(ViewerCommandCodec.EncodeCameraReset())));
        commands.Children.Add(CreateCommandButton(
            Localization.T("main.perspective"),
            () => _sendCommand(ViewerCommandCodec.EncodeCameraProjection(false))));
        commands.Children.Add(CreateCommandButton(
            Localization.T("main.orthographic"),
            () => _sendCommand(ViewerCommandCodec.EncodeCameraProjection(true))));
        commands.Children.Add(CreateCommandButton(Localization.T("main.focusSelected"), FocusSelected));
        commands.Children.Add(_settingsButton);

        var toolbar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(10, 0, 10, 8)
        };
        toolbar.Children.Add(commands);
        Grid.SetColumn(_moveSpeedStatus, 1);
        toolbar.Children.Add(_moveSpeedStatus);
        Grid.SetRow(toolbar, 1);
        Children.Add(toolbar);

        _sceneStatus = new TextBlock
        {
            Text = Localization.T("main.waitTarget"),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 6, 12, 2),
            FontSize = 12
        };
        _performanceStatus = new TextBlock
        {
            Text = Localization.T("main.perfWaiting"),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 2, 12, 6),
            FontSize = 12,
            Opacity = 0.82
        };

        _sceneHost = new NativeSceneHost(_sendCommand, FocusSelected)
        {
            Margin = new Thickness(8, 0, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _sceneHost.StatusChanged += status => _sceneStatus.Text = Localization.Translate(status);
        _sceneHost.MoveSpeedChanged += speed =>
            _moveSpeedStatus.Text = Localization.Translate($"Speed {speed:0.##} u/s");
        _sceneHost.SizeChanged += (_, e) => ScheduleViewportResize(e.NewSize);
        Grid.SetRow(_sceneHost, 2);
        Children.Add(_sceneHost);

        var statusBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(220, 24, 24, 24)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Children = { _sceneStatus, _performanceStatus }
            }
        };
        Grid.SetRow(statusBorder, 3);
        Children.Add(statusBorder);

        _resizeDebounce.Interval = TimeSpan.FromMilliseconds(250);
        _resizeDebounce.Tick += (_, _) =>
        {
            _resizeDebounce.Stop();
            ApplyAutoViewportSize();
        };

        Localization.LanguageChanged += OnLanguageChanged;
    }

    public void ApplySnapshot(RenderTargetInfo? target, PerformanceInfo performance)
    {
        _latestTarget = target;
        if (target is not null)
        {
            _moveSpeedStatus.Text = Localization.Translate($"Speed {target.MoveSpeed:0.##} u/s");
        }

        UpdatePerformanceStatus(performance);
        _sceneHost.SetRenderTarget(target);

        if (target is null)
        {
            return;
        }

        if (!_savedProfileApplied)
        {
            _savedProfileApplied = true;
            if (_savedProfile is not null)
            {
                ApplyProfile(_savedProfile, target);
                return;
            }
        }

        if (_autoViewport)
        {
            ScheduleViewportResize(_sceneHost.Bounds.Size);
        }
    }

    public void SetDisconnected()
    {
        _latestTarget = null;
        _savedProfileApplied = false;
        _requestedAutoWidth = 0;
        _requestedAutoHeight = 0;
        _resizeDebounce.Stop();
        _performanceStatus.Text = Localization.T("main.perfWaiting");
        _sceneHost.SetRenderTarget(null);
    }

    public void Shutdown()
    {
        Localization.LanguageChanged -= OnLanguageChanged;
        _resizeDebounce.Stop();
        _sceneHost.Shutdown();
    }

    private void OnLanguageChanged()
    {
        _settingsButton.Content = SettingsLabel();
        if (_latestTarget is not null)
        {
            _moveSpeedStatus.Text = Localization.Translate($"Speed {_latestTarget.MoveSpeed:0.##} u/s");
        }
    }

    private void FocusSelected()
    {
        if (_selectedInstanceId() is int instanceId)
        {
            _sendCommand(ViewerCommandCodec.EncodeCameraFocus(instanceId));
            return;
        }

        _setDetail(Localization.T("main.focusFirst"));
    }

    private void ShowSettings()
    {
        var target = _latestTarget;
        if (target is null)
        {
            _setDetail(Localization.T("main.waitTarget"));
            return;
        }

        var dialog = new SceneSettingsWindow(target, _autoViewport, _savedProfile, ApplySettings);
        _ = dialog.ShowDialog(_owner);
    }

    private void ApplySettings(SceneSettingsValues values)
    {
        var profile = new SceneSettingsProfile
        {
            FieldOfView = values.FieldOfView,
            NearClip = values.NearClip,
            FarClip = values.FarClip,
            OrthographicSize = values.OrthographicSize,
            IdleFps = values.IdleFps,
            InteractiveFps = values.InteractiveFps,
            AutoViewport = values.AutoViewport,
            Width = values.Width,
            Height = values.Height,
            CullingMode = values.CullingMode,
            CullingMask = values.CullingMask
        };

        _savedProfile = profile;
        _autoViewport = profile.AutoViewport;
        if (!string.IsNullOrWhiteSpace(_gameExecutablePath))
        {
            SceneSettingsStore.Save(_gameExecutablePath, profile);
        }

        if (_latestTarget is { } target)
        {
            ApplyProfile(profile, target);
        }
    }

    private void ApplyProfile(SceneSettingsProfile profile, RenderTargetInfo target)
    {
        _sendCommand(ViewerCommandCodec.EncodeCameraLens(
            profile.FieldOfView,
            profile.NearClip,
            profile.FarClip,
            profile.OrthographicSize));

        _autoViewport = profile.AutoViewport;
        _requestedAutoWidth = 0;
        _requestedAutoHeight = 0;

        if (_autoViewport)
        {
            var rawSize = _sceneHost.Bounds.Size;
            var size = rawSize.Width >= 1 && rawSize.Height >= 1
                ? NormalizeViewportSize(rawSize)
                : (target.Width, target.Height);
            _sendCommand(ViewerCommandCodec.EncodeCameraStreamSettings(
                profile.IdleFps,
                profile.InteractiveFps,
                size.Item1,
                size.Item2));
            _requestedAutoWidth = size.Item1;
            _requestedAutoHeight = size.Item2;
        }
        else
        {
            _resizeDebounce.Stop();
            _sendCommand(ViewerCommandCodec.EncodeCameraStreamSettings(
                profile.IdleFps,
                profile.InteractiveFps,
                profile.Width,
                profile.Height));
        }

        var mask = profile.CullingMode == SceneCullingMode.Manual ? profile.CullingMask : -1;
        _sendCommand(ViewerCommandCodec.EncodeCameraCulling(profile.CullingMode, mask));
    }

    private void ScheduleViewportResize(Size size)
    {
        if (!_autoViewport || _latestTarget is null || size.Width < 1 || size.Height < 1)
        {
            return;
        }

        _pendingViewportSize = size;
        _resizeDebounce.Stop();
        _resizeDebounce.Start();
    }

    private void ApplyAutoViewportSize()
    {
        var target = _latestTarget;
        if (!_autoViewport || target is null)
        {
            return;
        }

        var size = NormalizeViewportSize(_pendingViewportSize);
        if (size.Width == target.Width && size.Height == target.Height)
        {
            _requestedAutoWidth = 0;
            _requestedAutoHeight = 0;
            return;
        }

        if (size.Width == _requestedAutoWidth && size.Height == _requestedAutoHeight)
        {
            return;
        }

        _requestedAutoWidth = size.Width;
        _requestedAutoHeight = size.Height;
        _sendCommand(ViewerCommandCodec.EncodeCameraStreamSettings(
            _savedProfile?.IdleFps ?? target.IdleFps,
            _savedProfile?.InteractiveFps ?? target.InteractiveFps,
            size.Width,
            size.Height));
    }

    private static (int Width, int Height) NormalizeViewportSize(Size size)
    {
        var width = Math.Clamp((int)Math.Round(size.Width), MinRenderSize, MaxRenderSize);
        var height = Math.Clamp((int)Math.Round(size.Height), MinRenderSize, MaxRenderSize);
        return (width, height);
    }

    private void UpdatePerformanceStatus(PerformanceInfo performance)
    {
        var snapshotSize = performance.SnapshotBytes <= 0
            ? "n/a"
            : performance.SnapshotBytes >= 1024
                ? $"{performance.SnapshotBytes / 1024.0:0.0} KB"
                : $"{performance.SnapshotBytes} B";

        _performanceStatus.Text = Localization.IsChinese
            ? $"性能 · 游戏 {performance.GameFps:0.0} FPS · Scene {performance.SceneFps:0.0} FPS · 渲染 {performance.SceneRenderMs:0.00} ms · 层级 {performance.HierarchyNodes} 节点 / {performance.HierarchyScanMs:0.00} ms · JSON {performance.SnapshotSerializeMs:0.00} ms / {snapshotSize}"
            : $"Perf · Game {performance.GameFps:0.0} FPS · Scene {performance.SceneFps:0.0} FPS · Render {performance.SceneRenderMs:0.00} ms · Hierarchy {performance.HierarchyNodes} nodes / {performance.HierarchyScanMs:0.00} ms · JSON {performance.SnapshotSerializeMs:0.00} ms / {snapshotSize}";
    }

    private static string SettingsLabel() => Localization.IsChinese ? "设置…" : "Settings…";

    private static Button CreateCommandButton(string text, Action action)
    {
        var button = new Button { Content = text };
        button.Click += (_, _) => action();
        return button;
    }
}
