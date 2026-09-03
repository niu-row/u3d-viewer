using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace U3DViewer.Viewer;

internal sealed class ProcessPickerWindow : Window
{
    private readonly Action<UnityProcessInfo> _connect;
    private readonly ObservableCollection<UnityProcessInfo> _processes = new();
    private readonly ListBox _list;
    private readonly Button _connectButton;
    private readonly TextBlock _summary;
    private readonly DispatcherTimer _refreshTimer = new();
    private bool _refreshing;

    public ProcessPickerWindow(Action<UnityProcessInfo> connect)
    {
        _connect = connect;

        Title = "U3D Viewer - Select Unity Process";
        Width = 980;
        Height = 560;
        MinWidth = 760;
        MinHeight = 420;

        _list = new ListBox
        {
            ItemsSource = _processes,
            SelectionMode = SelectionMode.Single,
            ItemTemplate = new FuncDataTemplate<UnityProcessInfo>((item, _) => BuildProcessRow(item))
        };
        _list.SelectionChanged += (_, _) => UpdateSelection();

        _connectButton = new Button
        {
            Content = "Connect",
            IsEnabled = false,
            MinWidth = 100
        };
        _connectButton.Click += (_, _) => ConnectSelected();

        _summary = new TextBlock
        {
            Text = "Scanning running processes...",
            VerticalAlignment = VerticalAlignment.Center
        };

        Content = BuildLayout();

        _refreshTimer.Interval = TimeSpan.FromSeconds(2);
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();

        Opened += async (_, _) =>
        {
            await RefreshAsync();
            _refreshTimer.Start();
        };
        Closed += (_, _) => _refreshTimer.Stop();
    }

    private Control BuildLayout()
    {
        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            Margin = new Thickness(14)
        };

        root.Children.Add(new TextBlock
        {
            Text = "Select a running Unity game",
            FontSize = 22,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });

        var description = new TextBlock
        {
            Text = "All detected Unity standalone processes are listed. Only processes with a ready U3DViewer Agent can be connected.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(description, 1);
        root.Children.Add(description);

        var table = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*")
        };
        table.Children.Add(BuildHeader());

        var listBorder = new Border
        {
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            Child = _list
        };
        Grid.SetRow(listBorder, 1);
        table.Children.Add(listBorder);
        Grid.SetRow(table, 2);
        root.Children.Add(table);

        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Margin = new Thickness(0, 12, 0, 0)
        };
        footer.Children.Add(_summary);

        var refresh = new Button
        {
            Content = "Refresh",
            MinWidth = 90,
            Margin = new Thickness(8, 0)
        };
        refresh.Click += async (_, _) => await RefreshAsync();
        Grid.SetColumn(refresh, 1);
        footer.Children.Add(refresh);

        Grid.SetColumn(_connectButton, 2);
        footer.Children.Add(_connectButton);

        Grid.SetRow(footer, 3);
        root.Children.Add(footer);
        return root;
    }

    private static Control BuildHeader()
    {
        var grid = CreateColumns();
        grid.Margin = new Thickness(8, 0, 8, 6);
        AddCell(grid, "Process", 0, FontWeight.SemiBold);
        AddCell(grid, "PID", 1, FontWeight.SemiBold);
        AddCell(grid, "Backend", 2, FontWeight.SemiBold);
        AddCell(grid, "Agent", 3, FontWeight.SemiBold);
        AddCell(grid, "Path", 4, FontWeight.SemiBold);
        return grid;
    }

    private static Control BuildProcessRow(UnityProcessInfo item)
    {
        var grid = CreateColumns();
        grid.Margin = new Thickness(4, 5);
        AddCell(grid, item.ProcessName, 0);
        AddCell(grid, item.ProcessId.ToString(), 1);
        AddCell(grid, item.Backend, 2);

        var status = new TextBlock
        {
            Text = item.AgentStatusText,
            Foreground = item.AgentStatus switch
            {
                AgentProcessStatus.Ready => Brushes.Green,
                AgentProcessStatus.Busy => Brushes.Goldenrod,
                _ => Brushes.Gray
            },
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(status, 3);
        grid.Children.Add(status);

        AddCell(grid, item.ExecutablePath, 4);
        return grid;
    }

    private static Grid CreateColumns() => new()
    {
        ColumnDefinitions = new ColumnDefinitions("2*,80,100,120,3*")
    };

    private static void AddCell(Grid grid, string text, int column, FontWeight? weight = null)
    {
        var block = new TextBlock
        {
            Text = text,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = weight ?? FontWeight.Normal,
            Margin = new Thickness(4, 0)
        };
        Grid.SetColumn(block, column);
        grid.Children.Add(block);
    }

    private async Task RefreshAsync()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        var selectedPid = (_list.SelectedItem as UnityProcessInfo)?.ProcessId;

        try
        {
            var items = await Task.Run(UnityProcessDiscovery.Scan);
            _processes.Clear();
            foreach (var item in items)
            {
                _processes.Add(item);
            }

            if (selectedPid is int pid)
            {
                _list.SelectedItem = _processes.FirstOrDefault(item => item.ProcessId == pid);
            }

            var ready = items.Count(item => item.AgentStatus == AgentProcessStatus.Ready);
            _summary.Text = $"Found {items.Count} Unity process(es) · {ready} ready";
            UpdateSelection();
        }
        catch (Exception ex)
        {
            _summary.Text = $"Process scan failed: {ex.Message}";
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void UpdateSelection()
    {
        var selected = _list.SelectedItem as UnityProcessInfo;
        _connectButton.IsEnabled = selected?.AgentStatus == AgentProcessStatus.Ready;
    }

    private void ConnectSelected()
    {
        if (_list.SelectedItem is not UnityProcessInfo selected || selected.AgentStatus != AgentProcessStatus.Ready)
        {
            return;
        }

        _refreshTimer.Stop();
        _connect(selected);
    }
}
