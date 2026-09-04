using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using U3DViewer.Protocol;

namespace U3DViewer.Viewer;

internal sealed class ScenePanel : Grid
{
    private const int MinRenderSize = 64;
    private const int MaxRenderSize = 4096;
    private const int ViewportResizeTolerance = 2;
    private const double StatusFooterHeight = 48;

    private readonly Window _owner;
    private readonly Action<string> _sendCommand;
    private readonly Func<int?> _selectedInstanceId;
    private readonly Action<string> _setDetail;
    private readonly NativeSceneHost _sceneHost;
    private readonly TextBlock _sceneStatus;
    private readonly TextBlock _performanceStatus;
    private readonly TextBlock _moveSpeedStatus;
    private readonly Button _settingsButton;
    private readonly ToggleButton _perspectiveButton;
    private readonly ToggleButton _orthographicButton;
    private readonly CheckBox _followPositionBox;
    private readonly CheckBox _followRotationBox;
    private readonly DispatcherTimer _resizeDebounce = new();
    private readonly string _gameExecutablePath;

    private RenderTargetInfo? _latestTarget;
    private SceneSettingsProfile? _savedProfile;
    private bool _savedProfileApplied;
    private bool _autoViewport = true;
    private bool _autoViewportInitialized;
    private bool _followPosition;
    private bool _followRotation;
    private bool _updatingFollowControls;
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
        _followPosition = _savedProfile?.FollowMainCameraPosition ?? false;
        _followRotation = _savedProfile?.FollowMainCameraRotation ?? false;

        RowDefinitions = new RowDefinitions($"Auto,Auto,*,{StatusFooterHeight}");

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
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(10, 5, 2, 5)
        };

        _settingsButton = CreateCommandButton(SettingsLabel(), ShowSettings);
        _perspectiveButton = CreateProjectionButton(
            Localization.T("main.perspective"),
            orthographic: false);
        _orthographicButton = CreateProjectionButton(
            Localization.T("main.orthographic"),
            orthographic: true);

        _followPositionBox = new CheckBox
        {
            Content = FollowPositionLabel(),
            IsChecked = _followPosition,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 2)
        };
        _followRotationBox = new CheckBox
        {
            Content = FollowRotationLabel(),
            IsChecked = _followRotation,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2)
        };
        _followPositionBox.IsCheckedChanged += (_, _) => OnFollowChanged();
        _followRotationBox.IsCheckedChanged += (_, _) => OnFollowChanged();

        var commands = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(8, 0, 8, 6)
        };
        commands.Children.Add(CreateToolbarButton(
            Localization.T("main.resetCamera"),
            () => _sendCommand(ViewerCommandCodec.EncodeCameraReset())));
        commands.Children.Add(_perspectiveButton);
        commands.Children.Add(_orthographicButton);
        commands.Children.Add(CreateToolbarButton(Localization.T("main.focusSelected"), FocusSelected));
        _settingsButton.Margin = new Thickness(3);
        commands.Children.Add(_settingsButton);
        commands.Children.Add(_followPositionBox);
        commands.Children.Add(_followRotationBox);
        commands.Children.Add(_moveSpeedStatus);
        Grid.SetRow(commands, 1);
        Children.Add(commands);

        _sceneStatus = new TextBlock
        {
            Text = Localization.T("main.waitTarget"),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 3, 12, 1),
            FontSize = 12
        };
        _performanceStatus = new TextBlock
        {
            Text = Localization.T("main.perfWaiting"),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 1, 12, 3),
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
            Height = StatusFooterHeight,
            Background = new SolidColorBrush(Color.FromArgb(220, 24, 24, 24)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = new Grid
            {
                RowDefinitions = new RowDefinitions("*,*"),
                Children =
                {
                    _sceneStatus,
                    PlaceInRow(_performanceStatus, 1)
                }
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
            UpdateProjectionControls(target.Orthographic);
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

        // SizeChanged is the normal source of auto-viewport updates. The first valid target
        // needs one explicit synchronization because the host can be laid out before the
        // Agent connects, when ScheduleViewportResize intentionally ignores size events.
        if (_autoViewport && !_autoViewportInitialized)
        {
            _autoViewportInitialized = true;
            ScheduleViewportResize(_sceneHost.Bounds.Size);
        }
    }

    public void SetDisconnected()
    {
        _latestTarget = null;
        _savedProfileApplied = false;
        _autoViewportInitialized = false;
        _requestedAutoWidth = 0;
        _requestedAutoHeight = 0;
        _resizeDebounce.Stop();
        _performanceStatus.Text = Localization.T("main.perfWaiting");
        _perspectiveButton.IsChecked = false;
        _orthographicButton.IsChecked = false;
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
        _perspectiveButton.Content = Localization.T("main.perspective");
        _orthographicButton.Content = Localization.T("main.orthographic");
        _followPositionBox.Content = FollowPositionLabel();
        _followRotationBox.Content = FollowRotationLabel();
        if (_latestTarget is not null)
        {
            _moveSpeedStatus.Text = Localization.Translate($"Speed {_latestTarget.MoveSpeed:0.##} u/s");
        }
    }

    private void OnFollowChanged()
    {
        if (_updatingFollowControls)
        {
            return;
        }

        _followPosition = _followPositionBox.IsChecked == true;
        _followRotation = _followRotationBox.IsChecked == true;
        SaveFollowSettings();

        if (_latestTarget is not null)
        {
            _sendCommand(ViewerCommandCodec.EncodeCameraFollowTransform(_followPosition, _followRotation));
        }
    }

    private void SetProjection(bool orthographic)
    {
        UpdateProjectionControls(orthographic);
        _sendCommand(ViewerCommandCodec.EncodeCameraProjection(orthographic));
    }

    private void UpdateProjectionControls(bool orthographic)
    {
        _perspectiveButton.IsChecked = !orthographic;
        _orthographicButton.IsChecked = orthographic;
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
            CullingMask = values.CullingMask,
            FollowMainCameraPosition = _followPosition,
            FollowMainCameraRotation = _followRotation
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
        _followPosition = profile.FollowMainCameraPosition;
        _followRotation = profile.FollowMainCameraRotation;
        UpdateFollowControls();

        _sendCommand(ViewerCommandCodec.EncodeCameraLens(
            profile.FieldOfView,
            profile.NearClip,
            profile.FarClip,
            profile.OrthographicSize));

        _autoViewport = profile.AutoViewport;
        _autoViewportInitialized = false;
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
            _autoViewportInitialized = true;
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
        _sendCommand(ViewerCommandCodec.EncodeCameraFollowTransform(_followPosition, _followRotation));
    }

    private void SaveFollowSettings()
    {
        if (string.IsNullOrWhiteSpace(_gameExecutablePath))
        {
            return;
        }

        var profile = _savedProfile ?? CreateProfileFromCurrentState();
        profile.FollowMainCameraPosition = _followPosition;
        profile.FollowMainCameraRotation = _followRotation;
        _savedProfile = profile;
        SceneSettingsStore.Save(_gameExecutablePath, profile);
    }

    private SceneSettingsProfile CreateProfileFromCurrentState()
    {
        var target = _latestTarget;
        return new SceneSettingsProfile
        {
            FieldOfView = target?.FieldOfView ?? 60f,
            NearClip = target?.NearClipPlane ?? 0.001f,
            FarClip = target?.FarClipPlane ?? 10000f,
            OrthographicSize = target?.OrthographicSize ?? 5f,
            IdleFps = target?.IdleFps ?? 15f,
            InteractiveFps = target?.InteractiveFps ?? 30f,
            AutoViewport = _autoViewport,
            Width = target?.Width ?? 1280,
            Height = target?.Height ?? 720,
            CullingMode = target?.CullingMode ?? SceneCullingMode.MainCamera,
            CullingMask = target?.CullingMask ?? -1,
            FollowMainCameraPosition = _followPosition,
            FollowMainCameraRotation = _followRotation
        };
    }

    private void UpdateFollowControls()
    {
        _updatingFollowControls = true;
        try
        {
            _followPositionBox.IsChecked = _followPosition;
            _followRotationBox.IsChecked = _followRotation;
        }
        finally
        {
            _updatingFollowControls = false;
        }
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
        if (WithinViewportTolerance(size.Width, target.Width) &&
            WithinViewportTolerance(size.Height, target.Height))
        {
            _requestedAutoWidth = target.Width;
            _requestedAutoHeight = target.Height;
            return;
        }

        if (size.Width == _requestedAutoWidth && size.Height == _requestedAutoHeight)
        {
            return;
        }

        ViewerLog.Info(
            $"Scene auto viewport resize requested: {target.Width}x{target.Height} -> {size.Width}x{size.Height}.");
        _requestedAutoWidth = size.Width;
        _requestedAutoHeight = size.Height;
        _sendCommand(ViewerCommandCodec.EncodeCameraStreamSettings(
            _savedProfile?.IdleFps ?? target.IdleFps,
            _savedProfile?.InteractiveFps ?? target.InteractiveFps,
            size.Width,
            size.Height));
    }

    private static bool WithinViewportTolerance(int a, int b) =>
        Math.Abs(a - b) <= ViewportResizeTolerance;

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

    private ToggleButton CreateProjectionButton(string text, bool orthographic)
    {
        var button = new ToggleButton
        {
            Content = text,
            Margin = new Thickness(3)
        };
        button.Click += (_, _) => SetProjection(orthographic);
        return button;
    }

    private static Button CreateToolbarButton(string text, Action action)
    {
        var button = CreateCommandButton(text, action);
        button.Margin = new Thickness(3);
        return button;
    }

    private static T PlaceInRow<T>(T control, int row)
        where T : Control
    {
        Grid.SetRow(control, row);
        return control;
    }

    private static string SettingsLabel() => Localization.IsChinese ? "设置…" : "Settings…";
    private static string FollowPositionLabel() => Localization.IsChinese ? "跟随位置" : "Follow Position";
    private static string FollowRotationLabel() => Localization.IsChinese ? "跟随朝向" : "Follow Rotation";

    private static Button CreateCommandButton(string text, Action action)
    {
        var button = new Button { Content = text };
        button.Click += (_, _) => action();
        return button;
    }
}
