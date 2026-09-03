using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using U3DViewer.Protocol;

namespace U3DViewer.Viewer;

internal sealed class MainWindow : Window
{
    private readonly ViewerConnection _connection = new();
    private readonly TextBlock _connectionStatus;
    private readonly TextBlock _snapshotStatus;
    private readonly TextBlock _connectionDetail;
    private readonly HierarchyPanel _hierarchyPanel;
    private readonly InspectorPanel _inspectorPanel;
    private readonly ScenePanel _scenePanel;
    private bool _initialSceneCameraResetSent;

    public MainWindow()
    {
        Title = "U3D Viewer";
        Width = 1400;
        Height = 850;
        MinWidth = 1000;
        MinHeight = 600;

        _connectionStatus = new TextBlock
        {
            Text = Localization.T("main.disconnected"),
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeight.SemiBold
        };
        _snapshotStatus = new TextBlock
        {
            Text = Localization.T("main.noSnapshot"),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        _connectionDetail = new TextBlock
        {
            Text = Localization.T("main.waitAgent"),
            VerticalAlignment = VerticalAlignment.Center
        };

        _hierarchyPanel = new HierarchyPanel();
        _inspectorPanel = new InspectorPanel();
        _scenePanel = new ScenePanel(
            this,
            SendCommand,
            () => _hierarchyPanel.SelectedInstanceId,
            SetConnectionDetail);

        _hierarchyPanel.SelectionChanged += OnHierarchySelectionChanged;
        _hierarchyPanel.ExpansionChanged += (instanceId, expanded) =>
            SendCommand(ViewerCommandCodec.EncodeHierarchyExpanded(instanceId, expanded));

        Content = BuildLayout();

        _connection.StateChanged += state => Dispatcher.UIThread.Post(() => UpdateConnectionState(state));
        _connection.SnapshotReceived += snapshot => Dispatcher.UIThread.Post(() => ApplySnapshot(snapshot));
        _connection.Error += error => Dispatcher.UIThread.Post(() => _connectionDetail.Text = error.Message);

        Opened += (_, _) => _connection.Start();
        Closed += (_, _) =>
        {
            _hierarchyPanel.Shutdown();
            _inspectorPanel.Shutdown();
            _scenePanel.Shutdown();
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

        workspace.Children.Add(_hierarchyPanel);

        Grid.SetColumn(_scenePanel, 1);
        workspace.Children.Add(_scenePanel);

        Grid.SetColumn(_inspectorPanel, 2);
        workspace.Children.Add(_inspectorPanel);

        root.Children.Add(workspace);
        return root;
    }

    private void OnHierarchySelectionChanged(int instanceId, GameObjectInfo? gameObject)
    {
        SendCommand(ViewerCommandCodec.EncodeSelectObject(instanceId));
        _inspectorPanel.Show(gameObject);
    }

    private void ApplySnapshot(SceneSnapshot snapshot)
    {
        _snapshotStatus.Text = Localization.Translate(
            $"Snapshot #{snapshot.Sequence} · {snapshot.Scenes.Length} scene(s)");

        if (!_initialSceneCameraResetSent && snapshot.RenderTarget?.Available == true)
        {
            // The Agent can create its Scene Camera before the game's Camera.main is ready.
            // Reset once after the first usable target arrives so the initial pose is copied
            // at a more reliable point in the game's startup sequence. ScenePanel then
            // reapplies any per-game persisted lens/stream/culling settings afterwards.
            _initialSceneCameraResetSent = _connection.TrySendCommand(ViewerCommandCodec.EncodeCameraReset());
        }

        _hierarchyPanel.ApplyScenes(snapshot.Scenes);
        _inspectorPanel.Show(_hierarchyPanel.SelectedGameObject);
        _scenePanel.ApplySnapshot(snapshot.RenderTarget, snapshot.Performance);
    }

    private void UpdateConnectionState(ConnectionState state)
    {
        switch (state)
        {
            case ConnectionState.Connecting:
                _connectionStatus.Text = Localization.T("main.connecting");
                _connectionStatus.Foreground = Brushes.Goldenrod;
                _connectionDetail.Text = Localization.T("main.findPipe");
                break;

            case ConnectionState.Connected:
                _connectionStatus.Text = Localization.T("main.connected");
                _connectionStatus.Foreground = Brushes.Green;
                _connectionDetail.Text = Localization.T("main.receiving");
                break;

            default:
                _connectionStatus.Text = Localization.T("main.disconnected");
                _connectionStatus.Foreground = Brushes.Gray;
                _connectionDetail.Text = Localization.T("main.waitAgent");
                _initialSceneCameraResetSent = false;
                _hierarchyPanel.ResetConnectionState();
                _scenePanel.SetDisconnected();
                break;
        }
    }

    private void SendCommand(string command)
    {
        if (!_connection.TrySendCommand(command))
        {
            _connectionDetail.Text = Localization.T("main.commandNotSent");
        }
    }

    private void SetConnectionDetail(string text)
    {
        _connectionDetail.Text = text;
    }
}
