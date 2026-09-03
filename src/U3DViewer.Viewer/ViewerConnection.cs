using System.IO.Pipes;
using System.Text.Json;
using U3DViewer.Protocol;

namespace U3DViewer.Viewer;

internal enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected
}

internal sealed class ViewerConnection : IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private Task? _runTask;
    private ConnectionState _state = ConnectionState.Disconnected;

    public event Action<ConnectionState>? StateChanged;
    public event Action<SceneSnapshot>? SnapshotReceived;
    public event Action<Exception>? Error;

    public void Start()
    {
        if (_runTask is not null)
        {
            return;
        }

        _runTask = Task.Run(() => RunAsync(_shutdown.Token));
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            SetState(ConnectionState.Connecting);

            try
            {
                await using var pipe = new NamedPipeClientStream(
                    ".",
                    "u3d-viewer",
                    PipeDirection.In,
                    PipeOptions.Asynchronous);

                await pipe.ConnectAsync(1000, cancellationToken);
                SetState(ConnectionState.Connected);

                using var reader = new StreamReader(pipe);
                while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
                {
                    var line = await reader.ReadLineAsync(cancellationToken);
                    if (line is null)
                    {
                        break;
                    }

                    try
                    {
                        var snapshot = JsonSerializer.Deserialize<SceneSnapshot>(line, _jsonOptions);
                        if (snapshot is not null)
                        {
                            SnapshotReceived?.Invoke(snapshot);
                        }
                    }
                    catch (JsonException ex)
                    {
                        Error?.Invoke(ex);
                    }
                }
            }
            catch (TimeoutException)
            {
                // Agent is not running yet; retry quietly.
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException ex)
            {
                Error?.Invoke(ex);
            }
            catch (Exception ex)
            {
                Error?.Invoke(ex);
            }
            finally
            {
                SetState(ConnectionState.Disconnected);
            }

            try
            {
                await Task.Delay(500, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void SetState(ConnectionState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        StateChanged?.Invoke(state);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();

        if (_runTask is not null)
        {
            try
            {
                await _runTask;
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
        }

        _shutdown.Dispose();
    }
}
