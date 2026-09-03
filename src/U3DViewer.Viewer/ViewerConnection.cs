using System.IO.Pipes;
using System.Text;
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
    private readonly object _writerGate = new();

    private Task? _runTask;
    private ConnectionState _state = ConnectionState.Disconnected;
    private StreamWriter? _writer;

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

    public bool TrySendCommand(string command)
    {
        lock (_writerGate)
        {
            if (_writer is null)
            {
                return false;
            }

            try
            {
                _writer.WriteLine(command);
                return true;
            }
            catch (Exception ex)
            {
                Error?.Invoke(ex);
                return false;
            }
        }
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
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);

                await pipe.ConnectAsync(1000, cancellationToken);

                using var reader = new StreamReader(pipe, Encoding.UTF8, true, 16 * 1024, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
                {
                    AutoFlush = true
                };

                lock (_writerGate)
                {
                    _writer = writer;
                }

                SetState(ConnectionState.Connected);

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
                lock (_writerGate)
                {
                    _writer = null;
                }
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
