using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using U3DViewer.Protocol;

namespace U3DViewer.Viewer;

internal sealed class ScenePanel : Grid
{
    private readonly Window _owner;
    private readonly Action<string> _sendCommand;
    private readonly Func<int?> _selectedInstanceId;
    private readonly Action<string> _setDetail;
    private readonly NativeSceneHost _sceneHost;
    private readonly TextBlock _sceneStatus;
    private readonly TextBlock _performanceStatus;
    private readonly TextBlock _moveSpeedStatus;
    private readonly TextBox _fovBox;
    private readonly TextBox _nearBox;
    private readonly TextBox _farBox;
    private readonly TextBox _orthographicSizeBox;
    private readonly TextBox _idleFpsBox;
    private readonly TextBox _interactiveFpsBox;
    private readonly TextBox _renderWidthBox;
    private readonly TextBox _renderHeightBox;
    private readonly ComboBox _cullingSelector;
    private readonly Button _layersButton;
    private readonly TextBlock _maskStatus;

    private RenderTargetInfo? _latestTarget;
    private bool _syncingCulling;

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

        RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,*,Auto");

        Children.Add(new TextBlock
        {
            Text = Localization.T("main.scene"),
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(10, 10, 10, 8)
        });

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(10, 0, 10, 6)
        };
        toolbar.Children.Add(CreateCommandButton(Localization.T("main.resetCamera"), () => _sendCommand(ViewerCommandCodec.EncodeCameraReset())));
        toolbar.Children.Add(CreateCommandButton(Localization.T("main.perspective"), () => _sendCommand(ViewerCommandCodec.EncodeCameraProjection(false))));
        toolbar.Children.Add(CreateCommandButton(Localization.T("main.orthographic"), () => _sendCommand(ViewerCommandCodec.EncodeCameraProjection(true))));
        toolbar.Children.Add(CreateCommandButton(Localization.T("main.focusSelected"), FocusSelected));
        Grid.SetRow(toolbar, 1);
        Children.Add(toolbar);

        _fovBox = CreateValueTextBox("60");
        _nearBox = CreateValueTextBox("0.001");
        _farBox = CreateValueTextBox("10000");
        _orthographicSizeBox = CreateValueTextBox("5");
        _fovBox.LostFocus += (_, _) => ApplyLensFromControls();
        _nearBox.LostFocus += (_, _) => ApplyLensFromControls();
        _farBox.LostFocus += (_, _) => ApplyLensFromControls();
        _orthographicSizeBox.LostFocus += (_, _) => ApplyLensFromControls();

        var lensToolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(10, 0, 10, 8),
            VerticalAlignment = VerticalAlignment.Center
        };
        lensToolbar.Children.Add(CreateValueField("FOV", _fovBox));
        lensToolbar.Children.Add(CreateValueField(Localization.T("main.near"), _nearBox));
        lensToolbar.Children.Add(CreateValueField(Localization.T("main.far"), _farBox));
        lensToolbar.Children.Add(CreateValueField(Localization.T("main.orthoSize"), _orthographicSizeBox));
        lensToolbar.Children.Add(CreateCommandButton(Localization.T("main.applyLens"), ApplyLensFromControls));
        Grid.SetRow(lensToolbar, 2);
        Children.Add(lensToolbar);

        _idleFpsBox = CreateValueTextBox("15");
        _interactiveFpsBox = CreateValueTextBox("30");
        _renderWidthBox = CreateValueTextBox("1280");
        _renderHeightBox = CreateValueTextBox("720");
        _moveSpeedStatus = new TextBlock
        {
            Text = Localization.Translate("Speed 10 u/s"),
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeight.SemiBold
        };

        var streamToolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(10, 0, 10, 8),
            VerticalAlignment = VerticalAlignment.Center
        };
        streamToolbar.Children.Add(CreateValueField(Localization.T("main.idleFps"), _idleFpsBox));
        streamToolbar.Children.Add(CreateValueField(Localization.T("main.activeFps"), _interactiveFpsBox));
        streamToolbar.Children.Add(CreateValueField(Localization.T("main.width"), _renderWidthBox));
        streamToolbar.Children.Add(CreateValueField(Localization.T("main.height"), _renderHeightBox));
        streamToolbar.Children.Add(CreateCommandButton(Localization.T("main.applyStream"), ApplyStreamFromControls));
        streamToolbar.Children.Add(new Border { Width = 8 });
        streamToolbar.Children.Add(_moveSpeedStatus);
        Grid.SetRow(streamToolbar, 3);
        Children.Add(streamToolbar);

        _cullingSelector = new ComboBox
        {
            MinWidth = 160,
            VerticalAlignment = VerticalAlignment.Center
        };
        _cullingSelector.SelectionChanged += (_, _) => ApplyCullingSelection();
        _layersButton = new Button
        {
            Content = Localization.T("main.layers"),
            MinWidth = 100,
            IsEnabled = false
        };
        _layersButton.Click += (_, _) => ShowManualLayerEditor();
        _maskStatus = new TextBlock
        {
            Text = "0xFFFFFFFF",
            VerticalAlignment = VerticalAlignment.Center
        };

        var cullingToolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(10, 0, 10, 8),
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = Localization.T("main.cullingMask"),
                    FontWeight = FontWeight.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                },
                _cullingSelector,
                _layersButton,
                new TextBlock
                {
                    Text = Localization.T("main.mask"),
                    VerticalAlignment = VerticalAlignment.Center
                },
                _maskStatus
            }
        };
        Grid.SetRow(cullingToolbar, 4);
        Children.Add(cullingToolbar);
        RefreshCullingOptions();

        _sceneStatus = new TextBlock
        {
            Text = Localization.T("main.waitTarget"),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 900,
            Margin = new Thickness(14, 8, 14, 2)
        };
        _performanceStatus = new TextBlock
        {
            Text = Localization.T("main.perfWaiting"),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 1000,
            Margin = new Thickness(14, 2, 14, 8),
            FontSize = 12
        };

        _sceneHost = new NativeSceneHost(_sendCommand, FocusSelected)
        {
            Margin = new Thickness(10, 0, 10, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _sceneHost.StatusChanged += status => _sceneStatus.Text = Localization.Translate(status);
        _sceneHost.MoveSpeedChanged += speed =>
            _moveSpeedStatus.Text = Localization.Translate($"Speed {speed:0.##} u/s");
        Grid.SetRow(_sceneHost, 5);
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
        Grid.SetRow(statusBorder, 6);
        Children.Add(statusBorder);

        Localization.LanguageChanged += RefreshCullingOptions;
    }

    public void ApplySnapshot(RenderTargetInfo? target, PerformanceInfo performance)
    {
        _latestTarget = target;
        SyncLensControls(target);
        SyncStreamControls(target);
        SyncCullingControls(target);
        UpdatePerformanceStatus(performance);
        _sceneHost.SetRenderTarget(target);
    }

    public void SetDisconnected()
    {
        _latestTarget = null;
        _performanceStatus.Text = Localization.T("main.perfWaiting");
        _layersButton.IsEnabled = false;
        _sceneHost.SetRenderTarget(null);
    }

    public void Shutdown()
    {
        Localization.LanguageChanged -= RefreshCullingOptions;
        _sceneHost.Shutdown();
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

    private void ApplyLensFromControls()
    {
        if (!TryParseFloat(_fovBox.Text, out var fov) ||
            !TryParseFloat(_nearBox.Text, out var nearClip) ||
            !TryParseFloat(_farBox.Text, out var farClip) ||
            !TryParseFloat(_orthographicSizeBox.Text, out var orthographicSize) ||
            fov < 1f || fov > 179f || orthographicSize <= 0f || farClip <= nearClip)
        {
            _setDetail(Localization.T("main.invalidLens"));
            return;
        }

        _sendCommand(ViewerCommandCodec.EncodeCameraLens(fov, nearClip, farClip, orthographicSize));
    }

    private void ApplyStreamFromControls()
    {
        if (!TryParseFloat(_idleFpsBox.Text, out var idleFps) ||
            !TryParseFloat(_interactiveFpsBox.Text, out var interactiveFps) ||
            !int.TryParse(_renderWidthBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(_renderHeightBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) ||
            idleFps < 1f || idleFps > 120f || interactiveFps < 1f || interactiveFps > 120f ||
            width < 64 || width > 4096 || height < 64 || height > 4096)
        {
            _setDetail(Localization.T("main.invalidStream"));
            return;
        }

        _sendCommand(ViewerCommandCodec.EncodeCameraStreamSettings(idleFps, interactiveFps, width, height));
    }

    private void ApplyCullingSelection()
    {
        if (_syncingCulling || _cullingSelector.SelectedItem is not CullingModeOption option || _latestTarget is null)
        {
            return;
        }

        var mask = option.Mode == SceneCullingMode.Manual ? _latestTarget.CullingMask : -1;
        _sendCommand(ViewerCommandCodec.EncodeCameraCulling(option.Mode, mask));
    }

    private void SyncLensControls(RenderTargetInfo? target)
    {
        if (target is null)
        {
            return;
        }

        SyncFloatText(_fovBox, target.FieldOfView);
        SyncFloatText(_nearBox, target.NearClipPlane);
        SyncFloatText(_farBox, target.FarClipPlane);
        SyncFloatText(_orthographicSizeBox, target.OrthographicSize);
    }

    private void SyncStreamControls(RenderTargetInfo? target)
    {
        if (target is null)
        {
            return;
        }

        SyncFloatText(_idleFpsBox, target.IdleFps);
        SyncFloatText(_interactiveFpsBox, target.InteractiveFps);
        SyncIntegerText(_renderWidthBox, target.Width);
        SyncIntegerText(_renderHeightBox, target.Height);
        _moveSpeedStatus.Text = Localization.Translate($"Speed {target.MoveSpeed:0.##} u/s");
    }

    private void SyncCullingControls(RenderTargetInfo? target)
    {
        _layersButton.IsEnabled = target is not null;
        if (target is null)
        {
            return;
        }

        _maskStatus.Text = $"0x{unchecked((uint)target.CullingMask):X8}";
        _syncingCulling = true;
        var items = BuildModeOptions();
        _cullingSelector.ItemsSource = items;
        _cullingSelector.SelectedItem = items.First(option => option.Mode == target.CullingMode);
        _syncingCulling = false;
    }

    private void RefreshCullingOptions()
    {
        var mode = _latestTarget?.CullingMode ?? SceneCullingMode.MainCamera;
        _syncingCulling = true;
        var items = BuildModeOptions();
        _cullingSelector.ItemsSource = items;
        _cullingSelector.SelectedItem = items.First(option => option.Mode == mode);
        _syncingCulling = false;
    }

    private void UpdatePerformanceStatus(PerformanceInfo performance)
    {
        var snapshotSize = performance.SnapshotBytes <= 0
            ? "n/a"
            : performance.SnapshotBytes >= 1024
                ? $"{performance.SnapshotBytes / 1024.0:0.0} KB"
                : $"{performance.SnapshotBytes} B";

        _performanceStatus.Text = Localization.Translate(
            $"Perf · Render {performance.SceneRenderMs:0.00} ms " +
            $"(avg {performance.SceneRenderAverageMs:0.00}, max {performance.SceneRenderMaxMs:0.00}) · " +
            $"Hierarchy {performance.HierarchyNodes} nodes / {performance.HierarchyScanMs:0.00} ms " +
            $"(avg {performance.HierarchyScanAverageMs:0.00}, max {performance.HierarchyScanMaxMs:0.00}) · " +
            $"JSON {performance.SnapshotSerializeMs:0.00} ms / {snapshotSize}");
    }

    private void ShowManualLayerEditor()
    {
        var target = _latestTarget;
        if (target is null)
        {
            return;
        }

        var checkBoxes = new CheckBox[32];
        var layersGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", 16))),
            Margin = new Thickness(12)
        };

        var unsignedMask = unchecked((uint)target.CullingMask);
        for (var layer = 0; layer < 32; layer++)
        {
            var layerName = target.LayerNames.Length > layer ? target.LayerNames[layer] : string.Empty;
            if (string.IsNullOrWhiteSpace(layerName))
            {
                layerName = $"Layer {layer}";
            }

            var checkBox = new CheckBox
            {
                Content = $"{layer}: {layerName}",
                IsChecked = (unsignedMask & (1u << layer)) != 0,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 2)
            };
            checkBoxes[layer] = checkBox;
            Grid.SetColumn(checkBox, layer / 16);
            Grid.SetRow(checkBox, layer % 16);
            layersGrid.Children.Add(checkBox);
        }

        var dialog = new Window
        {
            Title = Localization.T("main.manualCullingTitle"),
            Width = 560,
            Height = 610,
            MinWidth = 460,
            MinHeight = 440,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var everythingButton = new Button { Content = Localization.T("main.everything") };
        everythingButton.Click += (_, _) =>
        {
            foreach (var checkBox in checkBoxes)
            {
                checkBox.IsChecked = true;
            }
        };

        var nothingButton = new Button { Content = Localization.T("main.nothing") };
        nothingButton.Click += (_, _) =>
        {
            foreach (var checkBox in checkBoxes)
            {
                checkBox.IsChecked = false;
            }
        };

        var cancelButton = new Button { Content = Localization.T("main.cancel") };
        cancelButton.Click += (_, _) => dialog.Close();

        var applyButton = new Button { Content = Localization.T("main.applyManualMask") };
        applyButton.Click += (_, _) =>
        {
            uint mask = 0;
            for (var layer = 0; layer < checkBoxes.Length; layer++)
            {
                if (checkBoxes[layer].IsChecked == true)
                {
                    mask |= 1u << layer;
                }
            }

            _sendCommand(ViewerCommandCodec.EncodeCameraCulling(SceneCullingMode.Manual, unchecked((int)mask)));
            dialog.Close();
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { everythingButton, nothingButton, cancelButton, applyButton }
        };

        var content = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        content.Children.Add(new TextBlock
        {
            Text = Localization.T("main.cullingDescription"),
            Margin = new Thickness(12, 12, 12, 4),
            TextWrapping = TextWrapping.Wrap
        });
        var scroll = new ScrollViewer { Content = layersGrid };
        Grid.SetRow(scroll, 1);
        content.Children.Add(scroll);
        Grid.SetRow(actions, 2);
        content.Children.Add(actions);
        dialog.Content = content;

        _ = dialog.ShowDialog(_owner);
    }

    private static CullingModeOption[] BuildModeOptions() =>
        new[]
        {
            new CullingModeOption(SceneCullingMode.All, Localization.T("main.cullingAll")),
            new CullingModeOption(SceneCullingMode.MainCamera, Localization.T("main.cullingMainCamera")),
            new CullingModeOption(SceneCullingMode.Manual, Localization.T("main.cullingManual"))
        };

    private static Button CreateCommandButton(string text, Action action)
    {
        var button = new Button { Content = text };
        button.Click += (_, _) => action();
        return button;
    }

    private static TextBox CreateValueTextBox(string text) => new()
    {
        Text = text,
        Width = 72,
        HorizontalContentAlignment = HorizontalAlignment.Right
    };

    private static Control CreateValueField(string label, TextBox textBox)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center
        });
        panel.Children.Add(textBox);
        return panel;
    }

    private static bool TryParseFloat(string? text, out float value)
    {
        if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
            float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        value = 0f;
        return false;
    }

    private static void SyncFloatText(TextBox textBox, float value)
    {
        if (!textBox.IsFocused)
        {
            textBox.Text = value.ToString("0.######", CultureInfo.InvariantCulture);
        }
    }

    private static void SyncIntegerText(TextBox textBox, int value)
    {
        if (!textBox.IsFocused)
        {
            textBox.Text = value.ToString(CultureInfo.InvariantCulture);
        }
    }

    private sealed class CullingModeOption
    {
        public CullingModeOption(SceneCullingMode mode, string label)
        {
            Mode = mode;
            Label = label;
        }

        public SceneCullingMode Mode { get; }
        public string Label { get; }
        public override string ToString() => Label;
    }
}
