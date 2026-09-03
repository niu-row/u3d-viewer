using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
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
    private readonly Channel<string> _commands = Channel.CreateBounded<string>(new BoundedChannelOptions(64)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropOldest
    });
    private readonly string _pipeName;

    private Task? _runTask;
    private ConnectionState _state = ConnectionState.Disconnected;

    public ViewerConnection()
    {
        _pipeName = ViewerSession.Target?.PipeName
            ?? throw new InvalidOperationException("No Unity process was selected before creating ViewerConnection.");
        Active = this;
    }

    internal static ViewerConnection? Active { get; private set; }

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
        if (_state != ConnectionState.Connected || string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        return _commands.Writer.TryWrite(command);
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
                    _pipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                await pipe.ConnectAsync(1000, cancellationToken);

                using var reader = new StreamReader(pipe, Encoding.UTF8, true, 16 * 1024, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
                {
                    AutoFlush = false
                };
                using var connectionShutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                SetState(ConnectionState.Connected);
                var writerTask = PumpCommandsAsync(writer, connectionShutdown.Token);

                try
                {
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
                finally
                {
                    connectionShutdown.Cancel();
                    try
                    {
                        await writerTask;
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (IOException)
                    {
                    }
                    DrainPendingCommands();
                }
            }
            catch (TimeoutException)
            {
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

    private async Task PumpCommandsAsync(StreamWriter writer, CancellationToken cancellationToken)
    {
        while (await _commands.Reader.WaitToReadAsync(cancellationToken))
        {
            var wroteAny = false;
            while (_commands.Reader.TryRead(out var command))
            {
                await writer.WriteLineAsync(command.AsMemory(), cancellationToken);
                wroteAny = true;
            }

            if (wroteAny)
            {
                await writer.FlushAsync(cancellationToken);
            }
        }
    }

    private void DrainPendingCommands()
    {
        while (_commands.Reader.TryRead(out _))
        {
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
        _commands.Writer.TryComplete();
        _shutdown.Cancel();

        if (_runTask is not null)
        {
            try
            {
                await _runTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (ReferenceEquals(Active, this))
        {
            Active = null;
        }

        _shutdown.Dispose();
    }
}
