using System.IO.Pipes;
using System.Text;
using BepInEx.Logging;
using U3DViewer.Protocol;

namespace U3DViewer.Agent.Mono;

internal sealed class PipeServer : IDisposable
{
    private readonly string _pipeName;
    private readonly ManualLogSource _log;
    private readonly Queue<string> _outbound = new Queue<string>();
    private readonly Queue<ViewerCommand> _inbound = new Queue<ViewerCommand>();
    private readonly object _outboundLock = new object();
    private readonly object _inboundLock = new object();
    private readonly AutoResetEvent _signal = new AutoResetEvent(false);
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
            Name = "U3DViewer.PipeServer"
        };
        _thread.Start();
    }

    public void Publish(string json)
    {
        if (!_viewerConnected)
        {
            return;
        }

        lock (_outboundLock)
        {
            _outbound.Enqueue(json);
            while (_outbound.Count > 2)
            {
                _outbound.Dequeue();
            }
        }
        _signal.Set();
    }

    public bool TryDequeueCommand(out ViewerCommand command)
    {
        lock (_inboundLock)
        {
            if (_inbound.Count == 0)
            {
                command = default(ViewerCommand);
                return false;
            }

            command = _inbound.Dequeue();
            return true;
        }
    }

    private bool TryDequeueOutbound(out string payload)
    {
        lock (_outboundLock)
        {
            if (_outbound.Count == 0)
            {
                payload = string.Empty;
                return false;
            }

            payload = _outbound.Dequeue();
            return true;
        }
    }

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
                    Name = "U3DViewer.PipeReader"
                };
                readerThread.Start();

                var writer = new StreamWriter(pipe, new UTF8Encoding(false))
                {
                    AutoFlush = true
                };

                while (!_stopping && pipe.IsConnected && Interlocked.CompareExchange(ref readerFinished, 0, 0) == 0)
                {
                    if (TryDequeueOutbound(out var payload))
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
                lock (_outboundLock) _outbound.Clear();
                lock (_inboundLock) _inbound.Clear();
            }
        }
    }

    private void ReadCommands(NamedPipeServerStream pipe)
    {
        var reader = new StreamReader(pipe, Encoding.UTF8);
        while (!_stopping && pipe.IsConnected)
        {
            var line = reader.ReadLine();
            if (line is null)
            {
                break;
            }

            if (ViewerCommandCodec.TryParse(line, out var command))
            {
                lock (_inboundLock)
                {
                    _inbound.Enqueue(command);
                }
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
        _signal.Close();
    }
}
