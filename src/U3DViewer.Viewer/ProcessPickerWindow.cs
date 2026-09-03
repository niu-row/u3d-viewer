using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace U3DViewer.Viewer;

internal sealed class ProcessPickerWindow : Window
{
    private readonly Action<UnityProcessInfo> _connect;
    private readonly ObservableCollection<UnityProcessInfo> _processes = new();
    private readonly ListBox _list;
    private readonly Button _actionButton;
    private readonly Button _openGameButton;
    private readonly Button _refreshButton;
    private readonly TextBlock _summary;
    private readonly ProgressBar _progressBar;
    private readonly DispatcherTimer _refreshTimer = new();
    private readonly DispatcherTimer _operationTimer = new();
    private DateTime _operationStartedUtc;
    private string _operationMessage = string.Empty;
    private bool _refreshing;
    private bool _operating;

    public ProcessPickerWindow(Action<UnityProcessInfo> connect)
    {
        _connect = connect;

        Title = "U3D Viewer - Select Unity Process";
        Width = 1040;
        Height = 580;
        MinWidth = 820;
        MinHeight = 440;

        _list = new ListBox
        {
            ItemsSource = _processes,
            SelectionMode = SelectionMode.Single,
            ItemTemplate = new FuncDataTemplate<UnityProcessInfo>((item, _) => BuildProcessRow(item))
        };
        _list.SelectionChanged += (_, _) => UpdateSelection();

        _actionButton = new Button
        {
            Content = "Select a process",
            IsEnabled = false,
            MinWidth = 140
        };
        _actionButton.Click += async (_, _) => await RunSelectedActionAsync();

        _openGameButton = new Button
        {
            Content = "Open Game…",
            MinWidth = 110
        };
        _openGameButton.Click += async (_, _) => await OpenGameAsync();

        _refreshButton = new Button
        {
            Content = "Refresh",
            MinWidth = 90,
            Margin = new Thickness(8, 0)
        };
        _refreshButton.Click += async (_, _) => await RefreshAsync();

        _summary = new TextBlock
        {
            Text = "Scanning running processes...",
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };

        _progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Height = 7,
            IsVisible = false,
            Margin = new Thickness(0, 7, 14, 0)
        };

        Content = BuildLayout();

        _refreshTimer.Interval = TimeSpan.FromSeconds(2);
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();

        _operationTimer.Interval = TimeSpan.FromMilliseconds(500);
        _operationTimer.Tick += (_, _) => UpdateOperationElapsed();

        Opened += async (_, _) =>
        {
            await RefreshAsync();
            _refreshTimer.Start();
        };
        Closed += (_, _) =>
        {
            _refreshTimer.Stop();
            _operationTimer.Stop();
        };
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
            Text = "Select or launch a Unity game",
            FontSize = 22,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });

        var description = new TextBlock
        {
            Text = "Choose a running Unity process or Open Game…. U3DViewer prepares BepInEx, builds or reuses the matching Agent, deploys it, launches/restarts the game, and connects automatically.",
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
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            Margin = new Thickness(0, 12, 0, 0)
        };
        footer.Children.Add(_summary);

        Grid.SetRow(_progressBar, 1);
        footer.Children.Add(_progressBar);

        Grid.SetColumn(_openGameButton, 1);
        Grid.SetRowSpan(_openGameButton, 2);
        footer.Children.Add(_openGameButton);

        Grid.SetColumn(_refreshButton, 2);
        Grid.SetRowSpan(_refreshButton, 2);
        footer.Children.Add(_refreshButton);

        Grid.SetColumn(_actionButton, 3);
        Grid.SetRowSpan(_actionButton, 2);
        footer.Children.Add(_actionButton);

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
        if (_refreshing || _operating)
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
        if (_operating)
        {
            _actionButton.IsEnabled = false;
            return;
        }

        if (_list.SelectedItem is not UnityProcessInfo selected)
        {
            _actionButton.Content = "Select a process";
            _actionButton.IsEnabled = false;
            return;
        }

        switch (selected.AgentStatus)
        {
            case AgentProcessStatus.Ready:
                _actionButton.Content = "Attach";
                _actionButton.IsEnabled = true;
                _summary.Text = $"{selected.ProcessName} · PID {selected.ProcessId} · Agent ready";
                break;

            case AgentProcessStatus.Busy:
                _actionButton.Content = "Agent Busy";
                _actionButton.IsEnabled = false;
                _summary.Text = "This Agent is already connected to another Viewer.";
                break;

            default:
                if (GameAutomation.CanInstall(selected, out var reason))
                {
                    _actionButton.Content = "Prepare + Restart";
                    _actionButton.IsEnabled = true;
                    _summary.Text = $"{selected.Backend} detected. U3DViewer can prepare the runtime and restart this game automatically.";
                }
                else
                {
                    _actionButton.Content = "Prepare unavailable";
                    _actionButton.IsEnabled = false;
                    _summary.Text = reason;
                }
                break;
        }
    }

    private async Task RunSelectedActionAsync()
    {
        if (_list.SelectedItem is not UnityProcessInfo selected)
        {
            return;
        }

        if (selected.AgentStatus == AgentProcessStatus.Ready)
        {
            _refreshTimer.Stop();
            _connect(selected);
            return;
        }

        if (selected.AgentStatus != AgentProcessStatus.NotDetected)
        {
            return;
        }

        await RunAutomationAsync(
            (token, progress) => GameAutomation.InstallAndRestartAsync(selected, progress, token));
    }

    private async Task OpenGameAsync()
    {
        if (_operating)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Unity game",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Windows executable")
                {
                    Patterns = new[] { "*.exe" }
                }
            }
        });

        var executablePath = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return;
        }

        await RunAutomationAsync(
            (token, progress) => GameAutomation.InstallLaunchAndWaitAsync(executablePath, progress, token));
    }

    private async Task RunAutomationAsync(
        Func<CancellationToken, IProgress<string>, Task<GameAutomationResult>> action)
    {
        _operating = true;
        _refreshTimer.Stop();
        _actionButton.IsEnabled = false;
        _openGameButton.IsEnabled = false;
        _refreshButton.IsEnabled = false;
        _list.IsEnabled = false;
        _operationStartedUtc = DateTime.UtcNow;
        _operationMessage = "Starting runtime preparation...";
        _progressBar.IsVisible = true;
        _progressBar.IsIndeterminate = false;
        _progressBar.Value = 2;
        _summary.Text = _operationMessage;
        _operationTimer.Start();

        string? terminalMessage = null;
        var progress = new Progress<string>(UpdateAutomationProgress);

        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var result = await action(cancellation.Token, progress);
            if (result.Success && result.Target is not null)
            {
                _operationMessage = "Agent ready. Opening Viewer...";
                _summary.Text = _operationMessage;
                _progressBar.IsIndeterminate = false;
                _progressBar.Value = 100;
                _connect(result.Target);
                return;
            }

            terminalMessage = result.Message;
            _summary.Text = terminalMessage;
        }
        catch (OperationCanceledException)
        {
            terminalMessage = "Operation timed out or was cancelled.";
            _summary.Text = terminalMessage;
        }
        catch (Exception ex)
        {
            terminalMessage = $"Operation failed: {ex.Message}";
            _summary.Text = terminalMessage;
        }
        finally
        {
            _operationTimer.Stop();
            _operating = false;
            _progressBar.IsIndeterminate = false;
            _progressBar.IsVisible = false;
            _openGameButton.IsEnabled = true;
            _refreshButton.IsEnabled = true;
            _list.IsEnabled = true;
            if (IsVisible)
            {
                _refreshTimer.Start();
                await RefreshAsync();
                if (!string.IsNullOrWhiteSpace(terminalMessage))
                {
                    _summary.Text = terminalMessage;
                }
            }
        }
    }

    private void UpdateAutomationProgress(string message)
    {
        _operationMessage = message;
        var state = ResolveProgressState(message);
        _progressBar.IsVisible = true;
        _progressBar.IsIndeterminate = state.Indeterminate;
        if (!state.Indeterminate)
        {
            _progressBar.Value = Math.Max(_progressBar.Value, state.Value);
        }

        UpdateOperationElapsed();
    }

    private void UpdateOperationElapsed()
    {
        if (!_operating || string.IsNullOrWhiteSpace(_operationMessage))
        {
            return;
        }

        var elapsed = DateTime.UtcNow - _operationStartedUtc;
        _summary.Text = $"{_operationMessage}  ·  {elapsed:mm\:ss}";
    }

    private static (double Value, bool Indeterminate) ResolveProgressState(string message)
    {
        if (message.Contains("Closing", StringComparison.OrdinalIgnoreCase)) return (5, false);
        if (message.Contains("Preparing Unity", StringComparison.OrdinalIgnoreCase)) return (10, false);
        if (message.Contains("Downloading BepInEx", StringComparison.OrdinalIgnoreCase)) return (15, true);
        if (message.Contains("Installing BepInEx", StringComparison.OrdinalIgnoreCase)) return (22, false);
        if (message.Contains("Starting the game once", StringComparison.OrdinalIgnoreCase)) return (30, true);
        if (message.Contains("interop assemblies generated", StringComparison.OrdinalIgnoreCase)) return (42, false);
        if (message.Contains("Checking", StringComparison.OrdinalIgnoreCase) &&
            message.Contains("cache", StringComparison.OrdinalIgnoreCase)) return (48, false);
        if (message.Contains("Using cached", StringComparison.OrdinalIgnoreCase)) return (72, false);
        if (message.Contains("build workspace", StringComparison.OrdinalIgnoreCase)) return (52, false);
        if (message.Contains("Building", StringComparison.OrdinalIgnoreCase) &&
            message.Contains("Agent", StringComparison.OrdinalIgnoreCase)) return (58, true);
        if (message.Contains("Cached", StringComparison.OrdinalIgnoreCase)) return (72, false);
        if (message.Contains("Deploying", StringComparison.OrdinalIgnoreCase)) return (82, false);
        if (message.Contains("Launching game", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("waiting for U3DViewer Agent", StringComparison.OrdinalIgnoreCase)) return (92, true);
        if (message.Contains("Agent ready", StringComparison.OrdinalIgnoreCase)) return (100, false);
        return (Math.Max(2, 0), false);
    }
}
