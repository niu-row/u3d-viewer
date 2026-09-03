using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using U3DViewer.Protocol;

namespace U3DViewer.Viewer;

internal sealed record SceneSettingsValues(
    float FieldOfView,
    float NearClip,
    float FarClip,
    float OrthographicSize,
    float IdleFps,
    float InteractiveFps,
    bool AutoViewport,
    int Width,
    int Height,
    SceneCullingMode CullingMode,
    int CullingMask);

internal sealed class SceneSettingsWindow : Window
{
    private readonly Action<SceneSettingsValues> _apply;
    private readonly string[] _layerNames;
    private readonly TextBox _fovBox;
    private readonly TextBox _nearBox;
    private readonly TextBox _farBox;
    private readonly TextBox _orthoSizeBox;
    private readonly TextBox _idleFpsBox;
    private readonly TextBox _activeFpsBox;
    private readonly CheckBox _autoViewportBox;
    private readonly TextBox _widthBox;
    private readonly TextBox _heightBox;
    private readonly ComboBox _cullingSelector;
    private readonly Button _layersButton;
    private readonly TextBlock _maskStatus;

    private int _manualMask;

    public SceneSettingsWindow(
        RenderTargetInfo target,
        bool autoViewport,
        SceneSettingsProfile? savedProfile,
        Action<SceneSettingsValues> apply)
    {
        _apply = apply;
        _layerNames = target.LayerNames ?? Array.Empty<string>();
        _manualMask = savedProfile?.CullingMask ?? target.CullingMask;

        Title = L("Scene Settings", "场景设置");
        Width = 520;
        Height = 610;
        MinWidth = 460;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _fovBox = CreateValueBox(savedProfile?.FieldOfView ?? target.FieldOfView);
        _nearBox = CreateValueBox(savedProfile?.NearClip ?? target.NearClipPlane);
        _farBox = CreateValueBox(savedProfile?.FarClip ?? target.FarClipPlane);
        _orthoSizeBox = CreateValueBox(savedProfile?.OrthographicSize ?? target.OrthographicSize);
        _idleFpsBox = CreateValueBox(savedProfile?.IdleFps ?? target.IdleFps);
        _activeFpsBox = CreateValueBox(savedProfile?.InteractiveFps ?? target.InteractiveFps);
        _widthBox = CreateValueBox(savedProfile?.Width ?? target.Width);
        _heightBox = CreateValueBox(savedProfile?.Height ?? target.Height);

        _autoViewportBox = new CheckBox
        {
            Content = L("Match Scene View size automatically", "自动匹配场景视图尺寸"),
            IsChecked = autoViewport,
            Margin = new Thickness(0, 4, 0, 2)
        };
        _autoViewportBox.IsCheckedChanged += (_, _) => UpdateResolutionEnabled();

        var cullingOptions = BuildCullingOptions();
        var initialMode = savedProfile?.CullingMode ?? target.CullingMode;
        _cullingSelector = new ComboBox
        {
            ItemsSource = cullingOptions,
            MinWidth = 190,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedItem = cullingOptions.First(item => item.Mode == initialMode)
        };

        _layersButton = new Button
        {
            Content = Localization.T("main.layers"),
            MinWidth = 100
        };
        _layersButton.Click += (_, _) => ShowLayerEditor();

        _maskStatus = new TextBlock
        {
            Text = FormatMask(_manualMask),
            VerticalAlignment = VerticalAlignment.Center
        };

        Content = BuildContent();
        UpdateResolutionEnabled();
    }

    private Control BuildContent()
    {
        var content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 14
        };

        content.Children.Add(BuildSection(
            L("Camera", "相机"),
            new[]
            {
                ("FOV", (Control)_fovBox),
                (Localization.T("main.near"), (Control)_nearBox),
                (Localization.T("main.far"), (Control)_farBox),
                (Localization.T("main.orthoSize"), (Control)_orthoSizeBox)
            }));

        var renderBody = new StackPanel { Spacing = 8 };
        renderBody.Children.Add(BuildFields(new[]
        {
            (Localization.T("main.idleFps"), (Control)_idleFpsBox),
            (Localization.T("main.activeFps"), (Control)_activeFpsBox)
        }));
        renderBody.Children.Add(_autoViewportBox);
        renderBody.Children.Add(new TextBlock
        {
            Text = L(
                "When enabled, the RenderTexture follows the actual Scene View width, height, and aspect ratio after resizing stops.",
                "开启后，RenderTexture 会在拖动停止后自动跟随场景视图的实际宽高和比例。"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72,
            FontSize = 12
        });
        renderBody.Children.Add(BuildFields(new[]
        {
            (Localization.T("main.width"), (Control)_widthBox),
            (Localization.T("main.height"), (Control)_heightBox)
        }));
        content.Children.Add(BuildSection(L("Rendering", "渲染"), renderBody));

        var visibility = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("140,*,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            RowSpacing = 8,
            ColumnSpacing = 8
        };
        visibility.Children.Add(CreateLabel(Localization.T("main.cullingMask"), 0));
        Grid.SetColumn(_cullingSelector, 1);
        visibility.Children.Add(_cullingSelector);
        Grid.SetColumn(_layersButton, 2);
        visibility.Children.Add(_layersButton);
        visibility.Children.Add(CreateLabel(Localization.T("main.mask"), 1));
        Grid.SetRow(_maskStatus, 1);
        Grid.SetColumn(_maskStatus, 1);
        Grid.SetColumnSpan(_maskStatus, 2);
        visibility.Children.Add(_maskStatus);
        content.Children.Add(BuildSection(L("Visibility", "可见性"), visibility));

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 4, 0, 0)
        };
        var cancel = new Button { Content = Localization.T("main.cancel"), MinWidth = 90 };
        cancel.Click += (_, _) => Close();
        var apply = new Button { Content = L("Apply", "应用"), MinWidth = 100 };
        apply.Click += (_, _) => ApplyAndClose();
        actions.Children.Add(cancel);
        actions.Children.Add(apply);
        content.Children.Add(actions);

        return new ScrollViewer { Content = content };
    }

    private static Border BuildSection(string title, Control body)
    {
        return new Border
        {
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        FontSize = 15,
                        FontWeight = FontWeight.SemiBold
                    },
                    body
                }
            }
        };
    }

    private static Border BuildSection(string title, (string Label, Control Control)[] fields) =>
        BuildSection(title, BuildFields(fields));

    private static Grid BuildFields((string Label, Control Control)[] fields)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("140,*"),
            RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", fields.Length))),
            RowSpacing = 8,
            ColumnSpacing = 8
        };

        for (var row = 0; row < fields.Length; row++)
        {
            grid.Children.Add(CreateLabel(fields[row].Label, row));
            Grid.SetRow(fields[row].Control, row);
            Grid.SetColumn(fields[row].Control, 1);
            grid.Children.Add(fields[row].Control);
        }

        return grid;
    }

    private static TextBlock CreateLabel(string text, int row)
    {
        var label = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(label, row);
        return label;
    }

    private static TextBox CreateValueBox(float value) => new()
    {
        Text = value.ToString("0.######", CultureInfo.InvariantCulture),
        HorizontalContentAlignment = HorizontalAlignment.Right
    };

    private static TextBox CreateValueBox(int value) => new()
    {
        Text = value.ToString(CultureInfo.InvariantCulture),
        HorizontalContentAlignment = HorizontalAlignment.Right
    };

    private void UpdateResolutionEnabled()
    {
        var manual = _autoViewportBox.IsChecked != true;
        _widthBox.IsEnabled = manual;
        _heightBox.IsEnabled = manual;
    }

    private void ApplyAndClose()
    {
        if (!TryParseFloat(_fovBox.Text, out var fov) ||
            !TryParseFloat(_nearBox.Text, out var nearClip) ||
            !TryParseFloat(_farBox.Text, out var farClip) ||
            !TryParseFloat(_orthoSizeBox.Text, out var orthoSize) ||
            !TryParseFloat(_idleFpsBox.Text, out var idleFps) ||
            !TryParseFloat(_activeFpsBox.Text, out var activeFps) ||
            !int.TryParse(_widthBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(_heightBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) ||
            fov < 1f || fov > 179f || orthoSize <= 0f || farClip <= nearClip ||
            idleFps < 1f || idleFps > 120f || activeFps < 1f || activeFps > 120f ||
            width < 64 || width > 4096 || height < 64 || height > 4096)
        {
            var error = new Window
            {
                Title = L("Invalid Scene Settings", "场景设置无效"),
                Width = 440,
                Height = 170,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new TextBlock
                {
                    Text = L(
                        "FOV must be 1-179, Ortho Size > 0, Far > Near, FPS 1-120, and manual resolution 64-4096.",
                        "FOV 必须为 1-179，正交尺寸 > 0，远裁剪 > 近裁剪，FPS 为 1-120，手动分辨率为 64-4096。"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(18)
                }
            };
            _ = error.ShowDialog(this);
            return;
        }

        var mode = _cullingSelector.SelectedItem is CullingModeOption option
            ? option.Mode
            : SceneCullingMode.MainCamera;

        _apply(new SceneSettingsValues(
            fov,
            nearClip,
            farClip,
            orthoSize,
            idleFps,
            activeFps,
            _autoViewportBox.IsChecked == true,
            width,
            height,
            mode,
            _manualMask));
        Close();
    }

    private void ShowLayerEditor()
    {
        var checkBoxes = new CheckBox[32];
        var layersGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", 16))),
            Margin = new Thickness(12)
        };

        var unsignedMask = unchecked((uint)_manualMask);
        for (var layer = 0; layer < 32; layer++)
        {
            var name = _layerNames.Length > layer ? _layerNames[layer] : string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = $"Layer {layer}";
            }

            var checkBox = new CheckBox
            {
                Content = $"{layer}: {name}",
                IsChecked = (unsignedMask & (1u << layer)) != 0,
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

        var everything = new Button { Content = Localization.T("main.everything") };
        everything.Click += (_, _) =>
        {
            foreach (var checkBox in checkBoxes) checkBox.IsChecked = true;
        };
        var nothing = new Button { Content = Localization.T("main.nothing") };
        nothing.Click += (_, _) =>
        {
            foreach (var checkBox in checkBoxes) checkBox.IsChecked = false;
        };
        var cancel = new Button { Content = Localization.T("main.cancel") };
        cancel.Click += (_, _) => dialog.Close();
        var apply = new Button { Content = Localization.T("main.applyManualMask") };
        apply.Click += (_, _) =>
        {
            uint mask = 0;
            for (var layer = 0; layer < checkBoxes.Length; layer++)
            {
                if (checkBoxes[layer].IsChecked == true) mask |= 1u << layer;
            }

            _manualMask = unchecked((int)mask);
            _maskStatus.Text = FormatMask(_manualMask);
            dialog.Close();
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { everything, nothing, cancel, apply }
        };
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        layout.Children.Add(new TextBlock
        {
            Text = Localization.T("main.cullingDescription"),
            Margin = new Thickness(12, 12, 12, 4),
            TextWrapping = TextWrapping.Wrap
        });
        var scroll = new ScrollViewer { Content = layersGrid };
        Grid.SetRow(scroll, 1);
        layout.Children.Add(scroll);
        Grid.SetRow(actions, 2);
        layout.Children.Add(actions);
        dialog.Content = layout;
        _ = dialog.ShowDialog(this);
    }

    private static CullingModeOption[] BuildCullingOptions() => new[]
    {
        new CullingModeOption(SceneCullingMode.All, Localization.T("main.cullingAll")),
        new CullingModeOption(SceneCullingMode.MainCamera, Localization.T("main.cullingMainCamera")),
        new CullingModeOption(SceneCullingMode.Manual, Localization.T("main.cullingManual"))
    };

    private static string FormatMask(int mask) => $"0x{unchecked((uint)mask):X8}";

    private static string L(string en, string zh) => Localization.IsChinese ? zh : en;

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
