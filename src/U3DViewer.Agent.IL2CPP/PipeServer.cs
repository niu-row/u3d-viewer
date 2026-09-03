using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using BepInEx.Logging;

namespace U3DViewer.Agent.IL2CPP;

internal sealed class PipeServer : IDisposable
{
    private readonly string _pipeName;
    private readonly ManualLogSource _log;
    private readonly ConcurrentQueue<string> _outbound = new();
    private readonly AutoResetEvent _signal = new(false);
    private Thread? _thread;
    private volatile bool _stopping;

    public PipeServer(string pipeName, ManualLogSource log)
    {
        _pipeName = pipeName;
        _log = log;
    }

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
        _outbound.Enqueue(json);
        while (_outbound.Count > 2 && _outbound.TryDequeue(out _)) { }
        _signal.Set();
    }

    private void Run()
    {
        while (!_stopping)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.Out,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.None);

                _log.LogInfo($"Waiting for viewer on pipe '{_pipeName}'...");
                pipe.WaitForConnection();
                _log.LogInfo("Viewer connected.");

                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 16 * 1024, leaveOpen: true)
                {
                    AutoFlush = true
                };

                while (!_stopping && pipe.IsConnected)
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
            }
            catch (IOException ex)
            {
                if (!_stopping) _log.LogWarning($"Viewer pipe disconnected: {ex.Message}");
            }
            catch (Exception ex)
            {
                if (!_stopping) _log.LogError($"Pipe server failed: {ex}");
            }
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _signal.Set();
        _thread?.Join(500);
        _signal.Dispose();
    }
}
