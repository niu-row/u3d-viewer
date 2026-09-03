using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using BepInEx.Logging;
using U3DViewer.Protocol;

namespace U3DViewer.Agent.IL2CPP;

internal sealed class PipeServer : IDisposable
{
    private readonly string _pipeName;
    private readonly ManualLogSource _log;
    private readonly ConcurrentQueue<string> _outbound = new();
    private readonly ConcurrentQueue<ViewerCommand> _inbound = new();
    private readonly AutoResetEvent _signal = new(false);
    private Thread? _thread;
    private NamedPipeServerStream? _activePipe;
    private volatile bool _stopping;
    private volatile bool _viewerConnected;

    public PipeServer(string pipeName, ManualLogSource log)
    {
        _pipeName = pipeName;
        _log = log;
    }

    public bool IsViewerConnected => _viewerConnected;

    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "U3DViewer.IL2CPP.PipeServer"
        };
        _thread.Start();
    }

    public void Publish(string json)
    {
        if (!_viewerConnected)
        {
            return;
        }

        // Hierarchy updates are incremental after the initial baseline. Preserve ordering:
        // dropping one delta would make all later deltas apply to the wrong Viewer state.
        _outbound.Enqueue(json);
        _signal.Set();
    }

    public bool TryDequeueCommand(out ViewerCommand command) => _inbound.TryDequeue(out command);

    private void Run()
    {
        while (!_stopping)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.None);

                _activePipe = pipe;
                _log.LogInfo($"Waiting for viewer on pipe '{_pipeName}'...");
                pipe.WaitForConnection();
                _viewerConnected = true;
                _log.LogInfo("Viewer connected.");

                var readerFinished = 0;
                var readerThread = new Thread(() =>
                {
                    try
                    {
                        ReadCommands(pipe);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref readerFinished, 1);
                        _signal.Set();
                    }
                })
                {
                    IsBackground = true,
                    Name = "U3DViewer.IL2CPP.PipeReader"
                };
                readerThread.Start();

                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 16 * 1024, leaveOpen: true)
                {
                    AutoFlush = true
                };

                while (!_stopping && pipe.IsConnected && Volatile.Read(ref readerFinished) == 0)
                {
                    if (_outbound.TryDequeue(out var payload))
                    {
                        writer.WriteLine(payload);
                    }
                    else
                    {
                        _signal.WaitOne(250);
                    }
                }

                readerThread.Join(250);
            }
            catch (IOException ex)
            {
                if (!_stopping) _log.LogWarning($"Viewer pipe disconnected: {ex.Message}");
            }
            catch (ObjectDisposedException) when (_stopping)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!_stopping) _log.LogError($"Pipe server failed: {ex}");
            }
            finally
            {
                _viewerConnected = false;
                _activePipe = null;
                while (_outbound.TryDequeue(out _)) { }
                while (_inbound.TryDequeue(out _)) { }
            }
        }
    }

    private void ReadCommands(NamedPipeServerStream pipe)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, true, 4096, leaveOpen: true);
        while (!_stopping && pipe.IsConnected)
        {
            var line = reader.ReadLine();
            if (line is null)
            {
                break;
            }

            if (ViewerCommandCodec.TryParse(line, out var command))
            {
                _inbound.Enqueue(command);
            }
            else
            {
                _log.LogDebug($"Ignored unknown viewer command: {line}");
            }
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _viewerConnected = false;
        _signal.Set();
        try { _activePipe?.Dispose(); } catch { }
        _thread?.Join(1000);
        _signal.Dispose();
    }
}
