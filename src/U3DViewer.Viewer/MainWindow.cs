using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using U3DViewer.Protocol;

namespace U3DViewer.Viewer;

internal sealed class MainWindow : Window
{
    private readonly ViewerConnection _connection = new();
    private readonly NativeSceneTextureReader _sceneReader = new();
    private readonly DispatcherTimer _sceneTimer = new();
    private readonly ObservableCollection<HierarchyNode> _rootNodes = new();
    private readonly TreeView _hierarchy;
    private readonly TextBlock _connectionStatus;
    private readonly TextBlock _snapshotStatus;
    private readonly TextBlock _connectionDetail;
    private readonly StackPanel _inspectorContent;
    private readonly Image _sceneImage;
    private readonly TextBlock _sceneStatus;

    private HierarchyNode? _selectedNode;
    private WriteableBitmap? _sceneBitmap;
    private int _sceneDxgiFormat;

    public MainWindow()
    {
        Title = "U3D Viewer";
        Width = 1400;
        Height = 850;
        MinWidth = 1000;
        MinHeight = 600;

        _connectionStatus = new TextBlock
        {
            Text = "● Disconnected",
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeight.SemiBold
        };

        _snapshotStatus = new TextBlock
        {
            Text = "No snapshot",
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        _connectionDetail = new TextBlock
        {
            Text = "Waiting for a U3DViewer Agent (Mono or IL2CPP)",
            VerticalAlignment = VerticalAlignment.Center
        };

        _hierarchy = new TreeView
        {
            ItemsSource = _rootNodes,
            SelectionMode = SelectionMode.Single,
            ItemTemplate = new FuncTreeDataTemplate<HierarchyNode>(
                BuildHierarchyHeader,
                node => node.Children)
        };
        _hierarchy.SelectionChanged += OnHierarchySelectionChanged;

        _inspectorContent = new StackPanel
        {
            Margin = new Thickness(12),
            Spacing = 6
        };
        RenderEmptyInspector();

        _sceneImage = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsVisible = false
        };

        _sceneStatus = new TextBlock
        {
            Text = "Waiting for the target game's Scene render target...",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 560,
            Margin = new Thickness(14)
        };

        _sceneTimer.Interval = TimeSpan.FromMilliseconds(33);
        _sceneTimer.Tick += (_, _) => RefreshSceneFrame();

        Content = BuildLayout();

        _connection.StateChanged += state => Dispatcher.UIThread.Post(() => UpdateConnectionState(state));
        _connection.SnapshotReceived += snapshot => Dispatcher.UIThread.Post(() => ApplySnapshot(snapshot));
        _connection.Error += error => Dispatcher.UIThread.Post(() => _connectionDetail.Text = error.Message);

        Opened += (_, _) =>
        {
            _connection.Start();
            _sceneTimer.Start();
        };

        Closed += (_, _) =>
        {
            _sceneTimer.Stop();
            _sceneReader.Dispose();
            _sceneBitmap?.Dispose();
            _sceneBitmap = null;
            _ = _connection.DisposeAsync().AsTask();
        };
    }

    private Control BuildLayout()
    {
        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*")
        };

        var statusBar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(10, 8)
        };
        statusBar.Children.Add(_connectionStatus);

        Grid.SetColumn(_connectionDetail, 1);
        _connectionDetail.Margin = new Thickness(16, 0);
        statusBar.Children.Add(_connectionDetail);

        Grid.SetColumn(_snapshotStatus, 2);
        statusBar.Children.Add(_snapshotStatus);

        root.Children.Add(new Border
        {
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = statusBar
        });

        var workspace = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("300,*,360")
        };
        Grid.SetRow(workspace, 1);

        workspace.Children.Add(BuildHierarchyPanel());

        var scene = BuildScenePanel();
        Grid.SetColumn(scene, 1);
        workspace.Children.Add(scene);

        var inspector = BuildInspectorPanel();
        Grid.SetColumn(inspector, 2);
        workspace.Children.Add(inspector);

        root.Children.Add(workspace);
        return root;
    }

    private Control BuildHierarchyPanel()
    {
        var panel = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*")
        };

        panel.Children.Add(new TextBlock
        {
            Text = "Runtime Hierarchy",
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(10, 10, 10, 8)
        });

        var scroll = new ScrollViewer
        {
            Content = _hierarchy
        };
        Grid.SetRow(scroll, 1);
        panel.Children.Add(scroll);

        return new Border
        {
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = panel
        };
    }

    private Control BuildScenePanel()
    {
        var panel = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*")
        };

        panel.Children.Add(new TextBlock
        {
            Text = "Scene View",
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(10, 10, 10, 8)
        });

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(10, 0, 10, 8)
        };
        toolbar.Children.Add(CreateCommandButton("Reset Camera", () => SendCameraCommand(ViewerCommandCodec.EncodeCameraReset())));
        toolbar.Children.Add(CreateCommandButton("Perspective", () => SendCameraCommand(ViewerCommandCodec.EncodeCameraProjection(false))));
        toolbar.Children.Add(CreateCommandButton("Orthographic", () => SendCameraCommand(ViewerCommandCodec.EncodeCameraProjection(true))));
        toolbar.Children.Add(CreateCommandButton("Focus Selected", FocusSelected));
        Grid.SetRow(toolbar, 1);
        panel.Children.Add(toolbar);

        var sceneSurface = new Grid();
        sceneSurface.Children.Add(_sceneImage);

        var statusBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(180, 24, 24, 24)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = _sceneStatus
        };
        sceneSurface.Children.Add(statusBorder);

        var inputSurface = new Border
        {
            Focusable = true,
            Margin = new Thickness(10),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            Background = Brushes.Black,
            Child = sceneSurface
        };
        inputSurface.PointerPressed += (_, _) => inputSurface.Focus();
        inputSurface.KeyDown += OnSceneKeyDown;
        Grid.SetRow(inputSurface, 2);
        panel.Children.Add(inputSurface);

        return panel;
    }

    private Control BuildInspectorPanel()
    {
        var panel = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*")
        };

        panel.Children.Add(new TextBlock
        {
            Text = "Runtime Inspector",
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(10, 10, 10, 8)
        });

        var scroll = new ScrollViewer
        {
            Content = _inspectorContent
        };
        Grid.SetRow(scroll, 1);
        panel.Children.Add(scroll);

        return new Border
        {
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1, 0, 0, 0),
            Child = panel
        };
    }

    private static Button CreateCommandButton(string text, Action action)
    {
        var button = new Button { Content = text };
        button.Click += (_, _) => action();
        return button;
    }

    private static Control BuildHierarchyHeader(HierarchyNode node, INameScope _)
    {
        var text = new TextBlock();
        text.Bind(TextBlock.TextProperty, new Binding(nameof(HierarchyNode.Label)));
        return text;
    }

    private void OnSceneKeyDown(object? sender, KeyEventArgs e)
    {
        string? command = e.Key switch
        {
            Key.W => ViewerCommandCodec.EncodeCameraMove(1f, 0f, 0f, 0.05f),
            Key.S => ViewerCommandCodec.EncodeCameraMove(-1f, 0f, 0f, 0.05f),
            Key.A => ViewerCommandCodec.EncodeCameraMove(0f, -1f, 0f, 0.05f),
            Key.D => ViewerCommandCodec.EncodeCameraMove(0f, 1f, 0f, 0.05f),
            Key.Q => ViewerCommandCodec.EncodeCameraMove(0f, 0f, -1f, 0.05f),
            Key.E => ViewerCommandCodec.EncodeCameraMove(0f, 0f, 1f, 0.05f),
            Key.Left => ViewerCommandCodec.EncodeCameraLook(-4f, 0f),
            Key.Right => ViewerCommandCodec.EncodeCameraLook(4f, 0f),
            Key.Up => ViewerCommandCodec.EncodeCameraLook(0f, -4f),
            Key.Down => ViewerCommandCodec.EncodeCameraLook(0f, 4f),
            _ => null
        };

        if (command is not null)
        {
            SendCameraCommand(command);
            e.Handled = true;
        }
    }

    private void FocusSelected()
    {
        if (_selectedNode?.InstanceId is int instanceId)
        {
            SendCameraCommand(ViewerCommandCodec.EncodeCameraFocus(instanceId));
        }
        else
        {
            _connectionDetail.Text = "Select a runtime GameObject before using Focus Selected.";
        }
    }

    private void SendCameraCommand(string command)
    {
        if (!_connection.TrySendCommand(command))
        {
            _connectionDetail.Text = "Camera command not sent: viewer is not connected to an agent.";
        }
    }

    private void UpdateConnectionState(ConnectionState state)
    {
        switch (state)
        {
            case ConnectionState.Connecting:
                _connectionStatus.Text = "● Connecting";
                _connectionStatus.Foreground = Brushes.Goldenrod;
                _connectionDetail.Text = "Looking for pipe 'u3d-viewer'";
                break;
            case ConnectionState.Connected:
                _connectionStatus.Text = "● Connected";
                _connectionStatus.Foreground = Brushes.Green;
                _connectionDetail.Text = "Receiving hierarchy and Scene Camera state";
                break;
            default:
                _connectionStatus.Text = "● Disconnected";
                _connectionStatus.Foreground = Brushes.Gray;
                _connectionDetail.Text = "Waiting for a U3DViewer Agent (Mono or IL2CPP)";
                _sceneReader.Reset();
                _sceneStatus.Text = "Waiting for the target game's Scene render target...";
                break;
        }
    }

    private void ApplySnapshot(SceneSnapshot snapshot)
    {
        _snapshotStatus.Text = $"Snapshot #{snapshot.Sequence} · {snapshot.Scenes.Length} scene(s)";
        UpdateRenderTarget(snapshot.RenderTarget);
        SyncScenes(snapshot.Scenes);

        if (_selectedNode?.GameObject is not null)
        {
            RenderInspector(_selectedNode.GameObject);
        }
    }

    private void UpdateRenderTarget(RenderTargetInfo? target)
    {
        if (target is null || !target.Available)
        {
            _sceneStatus.Text = target?.Status ?? "Agent did not publish Scene render target information.";
            return;
        }

        if (!_sceneReader.Open(target))
        {
            _sceneStatus.Text = _sceneReader.LastStatus;
            return;
        }

        if (_sceneBitmap is null ||
            _sceneBitmap.PixelSize.Width != target.Width ||
            _sceneBitmap.PixelSize.Height != target.Height ||
            _sceneDxgiFormat != target.DxgiFormat)
        {
            _sceneBitmap?.Dispose();
            _sceneDxgiFormat = target.DxgiFormat;

            var pixelFormat = target.DxgiFormat switch
            {
                87 or 91 => PixelFormats.Bgra8888,
                _ => PixelFormats.Rgba8888
            };

            _sceneBitmap = new WriteableBitmap(
                new PixelSize(target.Width, target.Height),
                new Vector(96, 96),
                pixelFormat,
                AlphaFormat.Opaque);
            _sceneImage.Source = _sceneBitmap;
        }

        _sceneStatus.Text = $"{target.Width}×{target.Height} · DXGI {target.DxgiFormat} · click Scene View, then use WASD/QE + arrow keys";
    }

    private void RefreshSceneFrame()
    {
        var bitmap = _sceneBitmap;
        if (bitmap is null)
        {
            return;
        }

        try
        {
            using var framebuffer = bitmap.Lock();
            if (_sceneReader.TryRead(
                    framebuffer.Address,
                    framebuffer.RowBytes,
                    framebuffer.Size.Height,
                    out var width,
                    out var height,
                    out var dxgiFormat))
            {
                _sceneImage.IsVisible = true;
                _sceneStatus.Text = $"LIVE · {width}×{height} · DXGI {dxgiFormat} · WASD/QE + arrow keys";
                _sceneImage.InvalidateVisual();
            }
        }
        catch (Exception ex)
        {
            _sceneStatus.Text = $"Scene frame update failed: {ex.Message}";
        }
    }

    private void SyncScenes(IReadOnlyList<SceneInfo> scenes)
    {
        var desiredKeys = new HashSet<string>();

        for (var index = 0; index < scenes.Count; index++)
        {
            var scene = scenes[index];
            var key = $"scene:{scene.BuildIndex}:{scene.Name}";
            desiredKeys.Add(key);

            var node = _rootNodes.FirstOrDefault(item => item.Key == key);
            if (node is null)
            {
                node = new HierarchyNode(key, null, $"Scene: {scene.Name}");
                _rootNodes.Insert(Math.Min(index, _rootNodes.Count), node);
            }
            else
            {
                var currentIndex = _rootNodes.IndexOf(node);
                if (currentIndex != index && index < _rootNodes.Count)
                {
                    _rootNodes.Move(currentIndex, index);
                }
            }

            node.Label = $"Scene: {scene.Name}  [build {scene.BuildIndex}]";
            SyncGameObjects(node.Children, scene.Roots);
        }

        for (var index = _rootNodes.Count - 1; index >= 0; index--)
        {
            if (!desiredKeys.Contains(_rootNodes[index].Key))
            {
                _rootNodes.RemoveAt(index);
            }
        }

        if (_selectedNode?.InstanceId is int selectedId)
        {
            var refreshed = FindByInstanceId(selectedId);
            if (refreshed is null)
            {
                _selectedNode = null;
                _hierarchy.SelectedItem = null;
                RenderEmptyInspector();
            }
            else if (!ReferenceEquals(refreshed, _selectedNode))
            {
                _selectedNode = refreshed;
                _hierarchy.SelectedItem = refreshed;
            }
        }
    }

    private static void SyncGameObjects(ObservableCollection<HierarchyNode> target, IReadOnlyList<GameObjectInfo> objects)
    {
        var desiredIds = new HashSet<int>();

        for (var index = 0; index < objects.Count; index++)
        {
            var gameObject = objects[index];
            desiredIds.Add(gameObject.InstanceId);

            var node = target.FirstOrDefault(item => item.InstanceId == gameObject.InstanceId);
            if (node is null)
            {
                node = new HierarchyNode($"go:{gameObject.InstanceId}", gameObject.InstanceId, gameObject.Name);
                target.Insert(Math.Min(index, target.Count), node);
            }
            else
            {
                var currentIndex = target.IndexOf(node);
                if (currentIndex != index && index < target.Count)
                {
                    target.Move(currentIndex, index);
                }
            }

            node.GameObject = gameObject;
            node.Label = gameObject.ActiveInHierarchy ? gameObject.Name : $"{gameObject.Name} (inactive)";
            SyncGameObjects(node.Children, gameObject.Children);
        }

        for (var index = target.Count - 1; index >= 0; index--)
        {
            var instanceId = target[index].InstanceId;
            if (instanceId is int id && !desiredIds.Contains(id))
            {
                target.RemoveAt(index);
            }
        }
    }

    private HierarchyNode? FindByInstanceId(int instanceId)
    {
        foreach (var root in _rootNodes)
        {
            var match = FindByInstanceId(root, instanceId);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static HierarchyNode? FindByInstanceId(HierarchyNode node, int instanceId)
    {
        if (node.InstanceId == instanceId)
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            var match = FindByInstanceId(child, instanceId);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private void OnHierarchySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_hierarchy.SelectedItem is HierarchyNode node && node.GameObject is not null)
        {
            _selectedNode = node;
            RenderInspector(node.GameObject);
        }
        else
        {
            _selectedNode = null;
            RenderEmptyInspector();
        }
    }

    private void RenderEmptyInspector()
    {
        _inspectorContent.Children.Clear();
        _inspectorContent.Children.Add(new TextBlock
        {
            Text = "Select a GameObject in Runtime Hierarchy.",
            TextWrapping = TextWrapping.Wrap
        });
    }

    private void RenderInspector(GameObjectInfo gameObject)
    {
        _inspectorContent.Children.Clear();

        AddInspectorHeading(gameObject.Name, 20);
        AddInspectorLine($"Instance ID: {gameObject.InstanceId}");
        AddInspectorLine($"Active: {gameObject.ActiveInHierarchy}  (self: {gameObject.ActiveSelf})");
        AddInspectorLine($"Layer: {gameObject.Layer}");
        AddInspectorLine($"Tag: {(string.IsNullOrWhiteSpace(gameObject.Tag) ? "<none>" : gameObject.Tag)}");

        AddInspectorHeading("Transform", 15);
        AddInspectorLine($"Position:       {FormatVector(gameObject.Transform.Position)}");
        AddInspectorLine($"Local Position: {FormatVector(gameObject.Transform.LocalPosition)}");
        AddInspectorLine($"Euler Angles:   {FormatVector(gameObject.Transform.EulerAngles)}");
        AddInspectorLine($"Local Scale:    {FormatVector(gameObject.Transform.LocalScale)}");

        AddInspectorHeading($"Components ({gameObject.Components.Length})", 15);
        if (gameObject.Components.Length == 0)
        {
            AddInspectorLine("<none>");
        }
        else
        {
            foreach (var component in gameObject.Components)
            {
                AddInspectorLine(component);
            }
        }
    }

    private void AddInspectorHeading(string text, double size)
    {
        _inspectorContent.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 8, 0, 2)
        });
    }

    private void AddInspectorLine(string text)
    {
        _inspectorContent.Children.Add(new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap
        });
    }

    private static string FormatVector(Vector3Info value) =>
        $"({value.X:0.###}, {value.Y:0.###}, {value.Z:0.###})";

    private sealed class HierarchyNode : INotifyPropertyChanged
    {
        private string _label;

        public HierarchyNode(string key, int? instanceId, string label)
        {
            Key = key;
            InstanceId = instanceId;
            _label = label;
        }

        public string Key { get; }
        public int? InstanceId { get; }
        public ObservableCollection<HierarchyNode> Children { get; } = new();
        public GameObjectInfo? GameObject { get; set; }

        public string Label
        {
            get => _label;
            set
            {
                if (_label == value)
                {
                    return;
                }

                _label = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
