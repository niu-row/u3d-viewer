using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
#if LEGACY_MONO
using System.Runtime.InteropServices;
#endif
using System.Text;
using System.Threading;
using BepInEx.Logging;
using U3DViewer.Protocol;

namespace U3DViewer.Agent.Mono;

internal sealed class PipeServer : IDisposable
{
#if LEGACY_MONO
    private const uint PipeAccessDuplex = 0x00000003;
    private const uint PipeTypeByte = 0x00000000;
    private const uint PipeReadModeByte = 0x00000000;
    private const uint PipeWait = 0x00000000;
    private const int ErrorBrokenPipe = 109;
    private const int ErrorNoData = 232;
    private const int ErrorPipeConnected = 535;
    private const int ErrorOperationAborted = 995;
    private const int ErrorInvalidHandle = 6;
    private const int NativePipeBufferSize = 64 * 1024;
    private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);
#endif

    private readonly string _pipeName;
    private readonly ManualLogSource _log;
    private readonly Queue<string> _outbound = new Queue<string>();
    private readonly Queue<ViewerCommand> _inbound = new Queue<ViewerCommand>();
    private readonly object _outboundLock = new object();
    private readonly object _inboundLock = new object();
    private readonly AutoResetEvent _signal = new AutoResetEvent(false);
    private Thread? _thread;
#if LEGACY_MONO
    private readonly object _nativePipeLock = new object();
    private IntPtr _activeNativePipe = IntPtr.Zero;
#else
    private NamedPipeServerStream? _activePipe;
#endif
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
#if LEGACY_MONO
        RunLegacyNative();
#else
        RunModern();
#endif
    }

#if LEGACY_MONO
    private void RunLegacyNative()
    {
        var path = @"\\.\pipe\" + _pipeName;
        var pipe = CreateNamedPipe(
            path,
            PipeAccessDuplex,
            PipeTypeByte | PipeReadModeByte | PipeWait,
            1,
            NativePipeBufferSize,
            NativePipeBufferSize,
            0,
            IntPtr.Zero);

        if (pipe == InvalidHandleValue || pipe == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            _log.LogError($"Legacy native pipe creation failed (Win32 {error}).");
            return;
        }

        lock (_nativePipeLock)
        {
            _activeNativePipe = pipe;
        }

        try
        {
            while (!_stopping)
            {
                ClearConnectionState();
                DisconnectNamedPipe(pipe);

                _log.LogInfo($"Waiting for viewer on native pipe '{_pipeName}'...");
                if (!ConnectNamedPipe(pipe, IntPtr.Zero))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != ErrorPipeConnected)
                    {
                        if (_stopping && (error == ErrorOperationAborted || error == ErrorInvalidHandle))
                        {
                            break;
                        }

                        if (!_stopping)
                        {
                            _log.LogWarning($"Legacy native pipe connect failed (Win32 {error}).");
                            Thread.Sleep(100);
                        }
                        continue;
                    }
                }

                if (_stopping)
                {
                    break;
                }

                _viewerConnected = true;
                _log.LogInfo("Viewer connected through native legacy pipe.");

                try
                {
                    ServeLegacyNativeConnection(pipe);
                }
                catch (Exception ex)
                {
                    if (!_stopping)
                    {
                        _log.LogWarning($"Legacy native pipe session ended: {ex.Message}");
                    }
                }
                finally
                {
                    _viewerConnected = false;
                    DisconnectNamedPipe(pipe);
                    ClearConnectionState();
                }
            }
        }
        finally
        {
            var shouldClose = false;
            lock (_nativePipeLock)
            {
                if (_activeNativePipe == pipe)
                {
                    _activeNativePipe = IntPtr.Zero;
                    shouldClose = true;
                }
            }

            if (shouldClose)
            {
                CloseHandle(pipe);
            }
            ClearConnectionState();
        }
    }

    private void ServeLegacyNativeConnection(IntPtr pipe)
    {
        var readerFinished = 0;
        var readerThread = new Thread(() =>
        {
            try
            {
                ReadCommandsNative(pipe);
            }
            finally
            {
                Interlocked.Exchange(ref readerFinished, 1);
                _signal.Set();
            }
        })
        {
            IsBackground = true,
            Name = "U3DViewer.NativePipeReader"
        };
        readerThread.Start();

        try
        {
            while (!_stopping && Interlocked.CompareExchange(ref readerFinished, 0, 0) == 0)
            {
                if (TryDequeueOutbound(out var payload))
                {
                    var bytes = Encoding.UTF8.GetBytes(payload + "\n");
                    if (!WriteNative(pipe, bytes))
                    {
                        break;
                    }
                }
                else
                {
                    _signal.WaitOne(250);
                }
            }
        }
        finally
        {
            DisconnectNamedPipe(pipe);
            readerThread.Join(500);
        }
    }

    private void ReadCommandsNative(IntPtr pipe)
    {
        var buffer = new byte[4096];
        using (var lineBuffer = new MemoryStream())
        {
            while (!_stopping)
            {
                int bytesRead;
                if (!ReadFile(pipe, buffer, buffer.Length, out bytesRead, IntPtr.Zero))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (!_stopping && error != ErrorBrokenPipe && error != ErrorNoData &&
                        error != ErrorOperationAborted && error != ErrorInvalidHandle)
                    {
                        _log.LogWarning($"Legacy native pipe read failed (Win32 {error}).");
                    }
                    break;
                }

                if (bytesRead <= 0)
                {
                    break;
                }

                for (var index = 0; index < bytesRead; index++)
                {
                    var value = buffer[index];
                    if (value == (byte)'\n')
                    {
                        var lineBytes = lineBuffer.ToArray();
                        lineBuffer.SetLength(0);
                        var length = lineBytes.Length;
                        if (length > 0 && lineBytes[length - 1] == (byte)'\r')
                        {
                            length--;
                        }

                        if (length > 0)
                        {
                            EnqueueCommand(Encoding.UTF8.GetString(lineBytes, 0, length));
                        }
                        continue;
                    }

                    lineBuffer.WriteByte(value);
                    if (lineBuffer.Length > NativePipeBufferSize)
                    {
                        lineBuffer.SetLength(0);
                        _log.LogWarning("Discarded an oversized legacy Viewer command.");
                    }
                }
            }
        }
    }

    private bool WriteNative(IntPtr pipe, byte[] bytes)
    {
        var offset = 0;
        while (offset < bytes.Length && !_stopping)
        {
            var remaining = bytes.Length - offset;
            var chunk = remaining;
            var send = bytes;
            if (offset != 0)
            {
                send = new byte[remaining];
                Buffer.BlockCopy(bytes, offset, send, 0, remaining);
            }

            int written;
            if (!WriteFile(pipe, send, chunk, out written, IntPtr.Zero) || written <= 0)
            {
                var error = Marshal.GetLastWin32Error();
                if (!_stopping && error != ErrorBrokenPipe && error != ErrorNoData &&
                    error != ErrorOperationAborted && error != ErrorInvalidHandle)
                {
                    _log.LogWarning($"Legacy native pipe write failed (Win32 {error}).");
                }
                return false;
            }

            offset += written;
        }
        return offset == bytes.Length;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateNamedPipe(
        string lpName,
        uint dwOpenMode,
        uint dwPipeMode,
        uint nMaxInstances,
        uint nOutBufferSize,
        uint nInBufferSize,
        uint nDefaultTimeOut,
        IntPtr lpSecurityAttributes);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConnectNamedPipe(IntPtr hNamedPipe, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DisconnectNamedPipe(IntPtr hNamedPipe);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(
        IntPtr hFile,
        byte[] lpBuffer,
        int nNumberOfBytesToRead,
        out int lpNumberOfBytesRead,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(
        IntPtr hFile,
        byte[] lpBuffer,
        int nNumberOfBytesToWrite,
        out int lpNumberOfBytesWritten,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
#else
    private void RunModern()
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
                ClearConnectionState();
                _activePipe = null;
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

            EnqueueCommand(line);
        }
    }
#endif

    private void ClearConnectionState()
    {
        _viewerConnected = false;
        lock (_outboundLock) _outbound.Clear();
        lock (_inboundLock) _inbound.Clear();
    }

    private void EnqueueCommand(string line)
    {
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

    public void Dispose()
    {
        _stopping = true;
        _viewerConnected = false;
        _signal.Set();
#if LEGACY_MONO
        IntPtr nativePipe;
        lock (_nativePipeLock)
        {
            nativePipe = _activeNativePipe;
            _activeNativePipe = IntPtr.Zero;
        }
        if (nativePipe != IntPtr.Zero && nativePipe != InvalidHandleValue)
        {
            CloseHandle(nativePipe);
        }
#else
        try { _activePipe?.Dispose(); } catch { }
#endif
        _thread?.Join(1000);
        _signal.Close();
    }
}
