using System.Runtime.InteropServices;

namespace U3DViewer.Viewer;

internal sealed class SceneFramePump : IDisposable
{
    private const int BytesPerPixel = 4;
    private static readonly TimeSpan EmptyPollDelay = TimeSpan.FromMilliseconds(4);

    private readonly NativeSceneTextureReader _reader;
    private readonly object _gate = new();

    private CancellationTokenSource? _cancellation;
    private Task? _worker;
    private BufferSlot[]? _slots;
    private int _width;
    private int _height;
    private int _stride;
    private int _latestIndex = -1;
    private int _latestWidth;
    private int _latestHeight;
    private int _latestDxgiFormat;
    private long _publishedSequence;
    private long _consumedSequence;

    public SceneFramePump(NativeSceneTextureReader reader)
    {
        _reader = reader;
    }

    public void Start(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            Stop();
            return;
        }

        lock (_gate)
        {
            if (_worker is { IsCompleted: false } && _width == width && _height == height)
            {
                return;
            }
        }

        Stop();

        var stride = checked(width * BytesPerPixel);
        var slots = new[]
        {
            new BufferSlot(checked(stride * height)),
            new BufferSlot(checked(stride * height))
        };
        var cancellation = new CancellationTokenSource();

        lock (_gate)
        {
            _width = width;
            _height = height;
            _stride = stride;
            _slots = slots;
            _latestIndex = -1;
            _latestWidth = 0;
            _latestHeight = 0;
            _latestDxgiFormat = 0;
            _publishedSequence = 0;
            _consumedSequence = 0;
            _cancellation = cancellation;
            _worker = Task.Run(() => ReadLoopAsync(cancellation.Token));
        }
    }

    public bool TryCopyLatest(
        IntPtr destination,
        int destinationStride,
        int destinationHeight,
        out int width,
        out int height,
        out int dxgiFormat)
    {
        width = 0;
        height = 0;
        dxgiFormat = 0;

        lock (_gate)
        {
            if (_slots is null ||
                _latestIndex < 0 ||
                _publishedSequence == _consumedSequence)
            {
                return false;
            }

            var frameWidth = _latestWidth;
            var frameHeight = _latestHeight;
            var rowBytes = checked(frameWidth * BytesPerPixel);
            if (frameWidth <= 0 ||
                frameHeight <= 0 ||
                destination == IntPtr.Zero ||
                destinationStride < rowBytes ||
                destinationHeight < frameHeight)
            {
                return false;
            }

            var display = _slots[_latestIndex].DisplayBytes;
            if (destinationStride == _stride && frameWidth == _width)
            {
                Marshal.Copy(display, 0, destination, checked(_stride * frameHeight));
            }
            else
            {
                for (var y = 0; y < frameHeight; y++)
                {
                    Marshal.Copy(
                        display,
                        checked(y * _stride),
                        IntPtr.Add(destination, checked(y * destinationStride)),
                        rowBytes);
                }
            }

            _consumedSequence = _publishedSequence;
            width = frameWidth;
            height = frameHeight;
            dxgiFormat = _latestDxgiFormat;
            return true;
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cancellation;
        Task? worker;

        lock (_gate)
        {
            cancellation = _cancellation;
            worker = _worker;
            _cancellation = null;
            _worker = null;
        }

        cancellation?.Cancel();
        if (worker is not null)
        {
            try
            {
                worker.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
            catch (Exception ex)
            {
                ViewerLog.Warning($"Scene frame worker stopped with an error: {ex}");
            }
        }

        BufferSlot[]? slots;
        lock (_gate)
        {
            slots = _slots;
            _slots = null;
            _latestIndex = -1;
            _publishedSequence = 0;
            _consumedSequence = 0;
        }

        if (slots is not null)
        {
            foreach (var slot in slots)
            {
                slot.Dispose();
            }
        }

        cancellation?.Dispose();
    }

    public void Dispose() => Stop();

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            BufferSlot? slot;
            int writeIndex;
            int stride;
            int height;

            lock (_gate)
            {
                if (_slots is null)
                {
                    return;
                }

                writeIndex = _latestIndex == 0 ? 1 : 0;
                slot = _slots[writeIndex];
                stride = _stride;
                height = _height;
            }

            if (_reader.TryRead(
                    slot.NativePointer,
                    stride,
                    height,
                    out var frameWidth,
                    out var frameHeight,
                    out var dxgiFormat))
            {
                FlipIntoDisplay(slot, frameWidth, frameHeight, stride);

                lock (_gate)
                {
                    if (_slots is null)
                    {
                        return;
                    }

                    _latestIndex = writeIndex;
                    _latestWidth = frameWidth;
                    _latestHeight = frameHeight;
                    _latestDxgiFormat = dxgiFormat;
                    _publishedSequence++;
                }

                await Task.Yield();
            }
            else
            {
                await Task.Delay(EmptyPollDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void FlipIntoDisplay(BufferSlot slot, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var rowBytes = checked(width * BytesPerPixel);
        for (var y = 0; y < height; y++)
        {
            var sourceY = height - 1 - y;
            Buffer.BlockCopy(
                slot.NativeBytes,
                checked(sourceY * stride),
                slot.DisplayBytes,
                checked(y * stride),
                rowBytes);
        }
    }

    private sealed class BufferSlot : IDisposable
    {
        private GCHandle _nativeHandle;

        public BufferSlot(int byteCount)
        {
            NativeBytes = new byte[byteCount];
            DisplayBytes = new byte[byteCount];
            _nativeHandle = GCHandle.Alloc(NativeBytes, GCHandleType.Pinned);
        }

        public byte[] NativeBytes { get; }
        public byte[] DisplayBytes { get; }
        public IntPtr NativePointer => _nativeHandle.AddrOfPinnedObject();

        public void Dispose()
        {
            if (_nativeHandle.IsAllocated)
            {
                _nativeHandle.Free();
            }
        }
    }
}
