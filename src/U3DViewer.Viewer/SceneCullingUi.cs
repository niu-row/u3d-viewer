using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using U3DViewer.Protocol;

namespace U3DViewer.Viewer;

internal static class SceneCullingUi
{
    public static void Attach(Window window)
    {
        var connection = ViewerConnection.Active;
        if (connection is null || window.Content is not Control originalContent)
        {
            return;
        }

        RenderTargetInfo? latestTarget = null;
        var syncing = false;
        var label = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeight.SemiBold
        };
        var selector = new ComboBox
        {
            MinWidth = 160,
            VerticalAlignment = VerticalAlignment.Center
        };
        var layersButton = new Button
        {
            MinWidth = 100,
            IsEnabled = false
        };
        var maskLabel = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center
        };
        var maskStatus = new TextBlock
        {
            Text = "0xFFFFFFFF",
            VerticalAlignment = VerticalAlignment.Center
        };

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(10, 5),
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                label,
                selector,
                layersButton,
                maskLabel,
                maskStatus
            }
        };

        var wrapper = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*")
        };
        wrapper.Children.Add(new Border
        {
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = bar
        });
        Grid.SetRow(originalContent, 1);
        wrapper.Children.Add(originalContent);
        window.Content = wrapper;

        void RefreshLanguage()
        {
            label.Text = Local("Culling Mask", "剔除遮罩");
            layersButton.Content = Local("Layers…", "图层…");
            maskLabel.Text = Local("Mask", "遮罩");

            var currentMode = latestTarget?.CullingMode ?? SceneCullingMode.MainCamera;
            syncing = true;
            var items = BuildModeOptions();
            selector.ItemsSource = items;
            selector.SelectedItem = items.First(option => option.Mode == currentMode);
            syncing = false;
        }

        void UpdateTarget(RenderTargetInfo? target)
        {
            latestTarget = target;
            layersButton.IsEnabled = target is not null;
            if (target is null)
            {
                return;
            }

            maskStatus.Text = $"0x{unchecked((uint)target.CullingMask):X8}";
            syncing = true;
            var items = BuildModeOptions();
            selector.ItemsSource = items;
            selector.SelectedItem = items.First(option => option.Mode == target.CullingMode);
            syncing = false;
        }

        selector.SelectionChanged += (_, _) =>
        {
            if (syncing || selector.SelectedItem is not CullingModeOption option || latestTarget is null)
            {
                return;
            }

            var mask = option.Mode == SceneCullingMode.Manual
                ? latestTarget.CullingMask
                : -1;
            connection.TrySendCommand(ViewerCommandCodec.EncodeCameraCulling(option.Mode, mask));
        };

        layersButton.Click += (_, _) =>
        {
            var target = latestTarget;
            if (target is not null)
            {
                ShowManualLayerEditor(window, target, connection);
            }
        };

        void OnSnapshot(SceneSnapshot snapshot) =>
            Dispatcher.UIThread.Post(() => UpdateTarget(snapshot.RenderTarget));

        connection.SnapshotReceived += OnSnapshot;
        Localization.LanguageChanged += RefreshLanguage;
        window.Closed += (_, _) =>
        {
            connection.SnapshotReceived -= OnSnapshot;
            Localization.LanguageChanged -= RefreshLanguage;
        };

        RefreshLanguage();
    }

    private static void ShowManualLayerEditor(
        Window owner,
        RenderTargetInfo target,
        ViewerConnection connection)
    {
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
            var layerName = target.LayerNames.Length > layer
                ? target.LayerNames[layer]
                : string.Empty;
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
            Title = Local("Manual Culling Mask", "手动剔除遮罩"),
            Width = 560,
            Height = 610,
            MinWidth = 460,
            MinHeight = 440,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var everythingButton = new Button { Content = Local("Everything", "全部") };
        everythingButton.Click += (_, _) =>
        {
            foreach (var checkBox in checkBoxes)
            {
                checkBox.IsChecked = true;
            }
        };

        var nothingButton = new Button { Content = Local("Nothing", "全不选") };
        nothingButton.Click += (_, _) =>
        {
            foreach (var checkBox in checkBoxes)
            {
                checkBox.IsChecked = false;
            }
        };

        var cancelButton = new Button { Content = Local("Cancel", "取消") };
        cancelButton.Click += (_, _) => dialog.Close();

        var applyButton = new Button { Content = Local("Apply Manual Mask", "应用手动遮罩") };
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

            connection.TrySendCommand(
                ViewerCommandCodec.EncodeCameraCulling(SceneCullingMode.Manual, unchecked((int)mask)));
            dialog.Close();
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Right,
            Children =
            {
                everythingButton,
                nothingButton,
                cancelButton,
                applyButton
            }
        };

        var content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto")
        };
        content.Children.Add(new TextBlock
        {
            Text = Local(
                "Choose the Unity Layers rendered by the Scene Camera.",
                "选择场景相机要渲染的 Unity Layer。"),
            Margin = new Thickness(12, 12, 12, 4),
            TextWrapping = TextWrapping.Wrap
        });
        var scroll = new ScrollViewer { Content = layersGrid };
        Grid.SetRow(scroll, 1);
        content.Children.Add(scroll);
        Grid.SetRow(actions, 2);
        content.Children.Add(actions);
        dialog.Content = content;

        _ = dialog.ShowDialog(owner);
    }

    private static CullingModeOption[] BuildModeOptions() =>
        new[]
        {
            new CullingModeOption(SceneCullingMode.All, Local("All", "所有")),
            new CullingModeOption(SceneCullingMode.MainCamera, Local("Copy Main Camera", "复制主相机")),
            new CullingModeOption(SceneCullingMode.Manual, Local("Manual", "手动"))
        };

    private static string Local(string english, string chinese) =>
        Localization.IsChinese ? chinese : english;

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
