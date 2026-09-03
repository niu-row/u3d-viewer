using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using U3DViewer.Protocol;

namespace U3DViewer.Viewer;

internal sealed class MainWindow : Window
{
    private const int MaxSceneBootstrapAttempts = 20;

    private readonly ViewerConnection _connection = new();
    private readonly TextBlock _connectionStatus;
    private readonly TextBlock _snapshotStatus;
    private readonly TextBlock _connectionDetail;
    private readonly HierarchyPanel _hierarchyPanel;
    private readonly InspectorPanel _inspectorPanel;
    private readonly ScenePanel _scenePanel;
    private readonly DispatcherTimer _sceneBootstrapTimer = new();
    private readonly object _snapshotDispatchLock = new();

    private SceneSnapshot? _pendingSnapshot;
    private bool _snapshotDispatchScheduled;
    private int _sceneBootstrapAttempts;
    private bool _sceneTargetReady;
    private bool _sceneVisibilitySent;
    private bool _lastSceneVisible;

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
        _hierarchyPanel.SceneExpansionChanged += (buildIndex, sceneName, expanded) =>
            SendCommand(ViewerCommandCodec.EncodeSceneExpanded(buildIndex, sceneName, expanded));

        _sceneBootstrapTimer.Interval = TimeSpan.FromMilliseconds(500);
        _sceneBootstrapTimer.Tick += (_, _) => BootstrapSceneCamera();

        Content = BuildLayout();

        _connection.StateChanged += state => Dispatcher.UIThread.Post(() => UpdateConnectionState(state));
        _connection.SnapshotReceived += QueueSnapshotForUi;
        _connection.Error += error => Dispatcher.UIThread.Post(() => _connectionDetail.Text = error.Message);

        PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty || e.Property == Visual.IsVisibleProperty)
            {
                UpdateSceneRenderVisibility();
            }
        };
        _scenePanel.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.IsVisibleProperty)
            {
                UpdateSceneRenderVisibility();
            }
        };

        Opened += (_, _) =>
        {
            _connection.Start();
            UpdateSceneRenderVisibility();
        };
        Closed += (_, _) =>
        {
            ClearPendingSnapshots();
            _sceneBootstrapTimer.Stop();
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

        var columns = new ColumnDefinitions("280,5,*,5,340");
        columns[0].MinWidth = 180;
        columns[2].MinWidth = 360;
        columns[4].MinWidth = 220;

        var workspace = new Grid
        {
            ColumnDefinitions = columns
        };
        Grid.SetRow(workspace, 1);

        workspace.Children.Add(_hierarchyPanel);

        var leftSplitter = CreateSplitter();
        Grid.SetColumn(leftSplitter, 1);
        workspace.Children.Add(leftSplitter);

        Grid.SetColumn(_scenePanel, 2);
        workspace.Children.Add(_scenePanel);

        var rightSplitter = CreateSplitter();
        Grid.SetColumn(rightSplitter, 3);
        workspace.Children.Add(rightSplitter);

        Grid.SetColumn(_inspectorPanel, 4);
        workspace.Children.Add(_inspectorPanel);

        root.Children.Add(workspace);
        return root;
    }

    private static GridSplitter CreateSplitter() => new()
    {
        Width = 5,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
        ResizeDirection = GridResizeDirection.Columns,
        ResizeBehavior = GridResizeBehavior.PreviousAndNext,
        Background = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255))
    };

    private void QueueSnapshotForUi(SceneSnapshot snapshot)
    {
        var schedule = false;
        lock (_snapshotDispatchLock)
        {
            // Snapshot delivery is state replacement, not an event history. When a large
            // hierarchy makes the Avalonia UI slower than the Agent, keep only the newest
            // state instead of building an unbounded Dispatcher backlog.
            _pendingSnapshot = snapshot;
            if (!_snapshotDispatchScheduled)
            {
                _snapshotDispatchScheduled = true;
                schedule = true;
            }
        }

        if (schedule)
        {
            Dispatcher.UIThread.Post(ApplyLatestSnapshot);
        }
    }

    private void ApplyLatestSnapshot()
    {
        SceneSnapshot? snapshot;
        lock (_snapshotDispatchLock)
        {
            snapshot = _pendingSnapshot;
            _pendingSnapshot = null;
        }

        try
        {
            if (snapshot is not null)
            {
                ApplySnapshot(snapshot);
            }
        }
        catch (Exception ex)
        {
            ViewerLog.Error($"Applying snapshot #{snapshot?.Sequence} to the UI failed.", ex);
            _connectionDetail.Text = $"Snapshot UI update failed: {ex.Message}";
        }
        finally
        {
            var reschedule = false;
            lock (_snapshotDispatchLock)
            {
                if (_pendingSnapshot is not null)
                {
                    reschedule = true;
                }
                else
                {
                    _snapshotDispatchScheduled = false;
                }
            }

            if (reschedule)
            {
                Dispatcher.UIThread.Post(ApplyLatestSnapshot);
            }
        }
    }

    private void ClearPendingSnapshots()
    {
        lock (_snapshotDispatchLock)
        {
            _pendingSnapshot = null;
        }
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

        if (snapshot.RenderTarget?.Available == true)
        {
            _sceneTargetReady = true;
            _sceneBootstrapTimer.Stop();
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
                _sceneVisibilitySent = false;
                UpdateSceneRenderVisibility();
                StartSceneBootstrap();
                break;

            default:
                ClearPendingSnapshots();
                _connectionStatus.Text = Localization.T("main.disconnected");
                _connectionStatus.Foreground = Brushes.Gray;
                _connectionDetail.Text = Localization.T("main.waitAgent");
                _sceneBootstrapTimer.Stop();
                _sceneBootstrapAttempts = 0;
                _sceneTargetReady = false;
                _sceneVisibilitySent = false;
                _hierarchyPanel.ResetConnectionState();
                _scenePanel.SetDisconnected();
                break;
        }
    }

    private void StartSceneBootstrap()
    {
        _sceneBootstrapTimer.Stop();
        _sceneBootstrapAttempts = 0;
        _sceneTargetReady = false;
        BootstrapSceneCamera();
        if (!_sceneTargetReady && _sceneBootstrapAttempts < MaxSceneBootstrapAttempts)
        {
            _sceneBootstrapTimer.Start();
        }
    }

    private void BootstrapSceneCamera()
    {
        if (_sceneTargetReady)
        {
            _sceneBootstrapTimer.Stop();
            return;
        }

        if (_sceneBootstrapAttempts >= MaxSceneBootstrapAttempts)
        {
            _sceneBootstrapTimer.Stop();
            return;
        }

        _sceneBootstrapAttempts++;
        _connection.TrySendCommand(ViewerCommandCodec.EncodeCameraReset());
    }

    private void UpdateSceneRenderVisibility()
    {
        var visible = IsVisible && this.WindowState != Avalonia.Controls.WindowState.Minimized && _scenePanel.IsVisible;
        if (_sceneVisibilitySent && visible == _lastSceneVisible)
        {
            return;
        }

        if (!_connection.TrySendCommand(ViewerCommandCodec.EncodeCameraVisibility(visible)))
        {
            return;
        }

        _sceneVisibilitySent = true;
        _lastSceneVisible = visible;
        ViewerLog.Info(visible
            ? "Scene Camera rendering resumed because the Viewer is visible."
            : "Scene Camera rendering paused because the Viewer is hidden or minimized.");
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
