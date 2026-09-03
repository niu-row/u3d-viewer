using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Platform;
using Avalonia.Threading;
using U3DViewer.Protocol;

namespace U3DViewer.Viewer;

internal sealed class NativeSceneHost : NativeControlHost
{
    private const string LibraryName = "U3DViewer.NativeBridge";
    private const float MouseSensitivity = 0.12f;
    private const float ShiftBoost = 4f;

    private readonly Action<string> _sendCommand;
    private readonly Action _focusSelected;
    private readonly DispatcherTimer _timer = new();
    private readonly DispatcherTimer _resizeRecoveryTimer = new();

    private IntPtr _hostWindow;
    private RenderTargetInfo? _target;
    private string _targetKey = string.Empty;
    private bool _presenterOpen;
    private int _presentInFlight;
    private int _resizePending;
    private DateTime _lastOpenAttemptUtc = DateTime.MinValue;
    private long _lastTickTimestamp = Stopwatch.GetTimestamp();
    private float _moveSpeed = 10f;
    private string _lastStatus = string.Empty;

    public NativeSceneHost(Action<string> sendCommand, Action focusSelected)
    {
        _sendCommand = sendCommand;
        _focusSelected = focusSelected;
        Focusable = true;

        _timer.Interval = TimeSpan.FromMilliseconds(16);
        _timer.Tick += OnTick;
        _timer.Start();

        _resizeRecoveryTimer.Interval = TimeSpan.FromMilliseconds(300);
        _resizeRecoveryTimer.Tick += OnResizeRecoveryTick;
        SizeChanged += OnHostSizeChanged;
    }

    public event Action<string>? StatusChanged;
    public event Action<float>? MoveSpeedChanged;

    public float MoveSpeed => _moveSpeed;

    public void SetRenderTarget(RenderTargetInfo? target)
    {
        if (target is null || !target.Available || string.IsNullOrWhiteSpace(target.SharedName))
        {
            _target = null;
            _targetKey = string.Empty;
            _resizeRecoveryTimer.Stop();
            Interlocked.Exchange(ref _resizePending, 0);
            ClosePresenter();
            SetStatus(target?.Status ?? "Waiting for the target game's Scene render target...");
            return;
        }

        if (target.MoveSpeed > 0f && Math.Abs(target.MoveSpeed - _moveSpeed) > 0.0001f)
        {
            _moveSpeed = Math.Clamp(target.MoveSpeed, 0.1f, 1000f);
            MoveSpeedChanged?.Invoke(_moveSpeed);
        }

        var key = $"{target.SharedName}|{target.AdapterLuid:X16}|{target.Width}x{target.Height}|{target.DxgiFormat}";
        if (!string.Equals(_targetKey, key, StringComparison.Ordinal))
        {
            ClosePresenter();
            _targetKey = key;
        }

        _target = target;
        if (_presenterOpen)
        {
            SetLiveStatus(target);
        }
        else
        {
            TryOpenPresenter(force: true);
        }
    }

    public void Shutdown()
    {
        _timer.Stop();
        _resizeRecoveryTimer.Stop();
        Interlocked.Exchange(ref _resizePending, 0);
        ClosePresenter();
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!OperatingSystem.IsWindows())
        {
            return base.CreateNativeControlCore(parent);
        }

        var handle = U3DViewer_CreateSceneHostWindow(parent.Handle);
        if (handle == IntPtr.Zero)
        {
            SetStatus("Could not create the native D3D11 Scene host window.");
            return base.CreateNativeControlCore(parent);
        }

        _hostWindow = handle;
        _lastTickTimestamp = Stopwatch.GetTimestamp();
        TryOpenPresenter(force: true);
        return new SceneHostWindowHandle(handle);
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (control.Handle == _hostWindow)
        {
            _resizeRecoveryTimer.Stop();
            Interlocked.Exchange(ref _resizePending, 0);
            ClosePresenter();
            _hostWindow = IntPtr.Zero;
        }

        base.DestroyNativeControlCore(control);
    }

    private void OnHostSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_hostWindow == IntPtr.Zero || e.NewSize.Width < 1 || e.NewSize.Height < 1)
        {
            return;
        }

        Interlocked.Exchange(ref _resizePending, 1);
        _resizeRecoveryTimer.Stop();
        _resizeRecoveryTimer.Start();
    }

    private void OnResizeRecoveryTick(object? sender, EventArgs e)
    {
        _resizeRecoveryTimer.Stop();

        if (_hostWindow == IntPtr.Zero || _target is null)
        {
            Interlocked.Exchange(ref _resizePending, 0);
            return;
        }

        // Never tear down the native presenter while a background Present call is still inside it.
        // A queued-but-not-started Present observes _resizePending and exits without touching DXGI.
        if (Volatile.Read(ref _presentInFlight) != 0)
        {
            _resizeRecoveryTimer.Start();
            return;
        }

        ClosePresenter();
        Interlocked.Exchange(ref _resizePending, 0);
        TryOpenPresenter(force: true);
        if (_presenterOpen)
        {
            QueuePresent();
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var nowTimestamp = Stopwatch.GetTimestamp();
        var deltaSeconds = (float)((nowTimestamp - _lastTickTimestamp) / (double)Stopwatch.Frequency);
        _lastTickTimestamp = nowTimestamp;
        deltaSeconds = Math.Clamp(deltaSeconds, 0f, 0.1f);

        if (_hostWindow == IntPtr.Zero)
        {
            return;
        }

        if (!_presenterOpen && _target is not null && Volatile.Read(ref _resizePending) == 0)
        {
            TryOpenPresenter(force: false);
        }

        if (_presenterOpen && Volatile.Read(ref _resizePending) == 0)
        {
            QueuePresent();
        }

        PollInput(deltaSeconds);
    }

    private void QueuePresent()
    {
        var window = _hostWindow;
        if (window == IntPtr.Zero || !_presenterOpen || Volatile.Read(ref _resizePending) != 0 ||
            Interlocked.CompareExchange(ref _presentInFlight, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            var presentResult = 0;
            var hresult = 0;
            Exception? failure = null;

            try
            {
                if (Volatile.Read(ref _resizePending) != 0)
                {
                    return;
                }

                presentResult = U3DViewer_PresentScene(window);
                if (presentResult < 0)
                {
                    hresult = U3DViewer_GetScenePresenterLastError(window);
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                Interlocked.Exchange(ref _presentInFlight, 0);
            }

            if (presentResult >= 0 && failure is null)
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (window != _hostWindow || !_presenterOpen)
                {
                    return;
                }

                if (failure is not null)
                {
                    SetStatus($"Scene GPU presentation failed: {failure.Message}. Retrying...");
                    ViewerLog.Error("Scene GPU presentation threw an exception.", failure);
                }
                else
                {
                    SetStatus($"Scene GPU presentation failed (HRESULT 0x{hresult:X8}). Retrying...");
                    ViewerLog.Error($"Scene GPU presentation failed (HRESULT 0x{hresult:X8}).");
                }
                ClosePresenter();
            });
        });
    }

    private void TryOpenPresenter(bool force)
    {
        var target = _target;
        if (_hostWindow == IntPtr.Zero || target is null || _presenterOpen || Volatile.Read(ref _resizePending) != 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (!force && now - _lastOpenAttemptUtc < TimeSpan.FromMilliseconds(250))
        {
            return;
        }
        _lastOpenAttemptUtc = now;

        try
        {
            var opened = U3DViewer_OpenScenePresenter(_hostWindow, target.SharedName, target.AdapterLuid);
            var presenterLuid = U3DViewer_GetScenePresenterAdapterLuid(_hostWindow);
            var presenterName = GetPresenterAdapterName();
            var gameGpu = FormatAdapter(target.AdapterName, target.AdapterLuid);
            var viewerGpu = FormatAdapter(presenterName, presenterLuid);

            if (opened == 0)
            {
                var hresult = U3DViewer_GetScenePresenterLastError(_hostWindow);
                var stage = DescribeInitStage(U3DViewer_GetScenePresenterInitStage(_hostWindow));
                SetStatus(
                    $"Could not open GPU Scene presenter at {stage} (HRESULT 0x{hresult:X8}, source DXGI {target.DxgiFormat}). " +
                    $"Game GPU: {gameGpu} · Viewer GPU: {viewerGpu}");
                return;
            }

            _presenterOpen = true;
            SetLiveStatus(target);
            ViewerLog.Info($"Scene zero-copy presenter opened. Game GPU: {gameGpu} · Viewer GPU: {viewerGpu}");
        }
        catch (EntryPointNotFoundException ex)
        {
            SetStatus($"NativeBridge presenter API mismatch: {ex.Message}. Rebuild U3DViewer.");
            ViewerLog.Error("NativeBridge presenter API mismatch.", ex);
        }
        catch (DllNotFoundException ex)
        {
            SetStatus("U3DViewer.NativeBridge.dll was not found next to U3DViewer.Viewer.exe.");
            ViewerLog.Error("NativeBridge was not found.", ex);
        }
        catch (Exception ex)
        {
            SetStatus($"Opening zero-copy Scene presenter failed: {ex.Message}");
            ViewerLog.Error("Opening zero-copy Scene presenter failed.", ex);
        }
    }

    private void SetLiveStatus(RenderTargetInfo target)
    {
        SetStatus(
            $"LIVE · {target.Width}×{target.Height} · DXGI {target.DxgiFormat} · " +
            "RMB + mouse look · RMB + WASD/QE fly · Shift boost · wheel speed · F focus");
    }

    private void PollInput(float deltaSeconds)
    {
        if (U3DViewer_PollSceneInput(_hostWindow, out var input) == 0)
        {
            return;
        }

        if (input.FocusPressed != 0)
        {
            _focusSelected();
        }

        if (input.WheelDelta != 0)
        {
            var multiplier = Math.Pow(1.25, input.WheelDelta);
            _moveSpeed = Math.Clamp((float)(_moveSpeed * multiplier), 0.1f, 1000f);
            MoveSpeedChanged?.Invoke(_moveSpeed);
            _sendCommand(ViewerCommandCodec.EncodeCameraSpeed(_moveSpeed));
        }

        if (input.RightMouse == 0)
        {
            return;
        }

        if (input.MouseDeltaX != 0 || input.MouseDeltaY != 0)
        {
            _sendCommand(ViewerCommandCodec.EncodeCameraLook(
                input.MouseDeltaX * MouseSensitivity,
                input.MouseDeltaY * MouseSensitivity));
        }

        var forward = (float)input.Forward;
        var right = (float)input.Right;
        var up = (float)input.Up;
        var magnitude = MathF.Sqrt(forward * forward + right * right + up * up);
        if (magnitude <= 0f || deltaSeconds <= 0f)
        {
            return;
        }

        if (magnitude > 1f)
        {
            forward /= magnitude;
            right /= magnitude;
            up /= magnitude;
        }

        if (input.Shift != 0)
        {
            deltaSeconds *= ShiftBoost;
        }

        _sendCommand(ViewerCommandCodec.EncodeCameraMove(forward, right, up, deltaSeconds));
    }

    private void ClosePresenter()
    {
        var wasOpen = _presenterOpen;
        _presenterOpen = false;
        if (_hostWindow != IntPtr.Zero && wasOpen)
        {
            try
            {
                U3DViewer_CloseScenePresenter(_hostWindow);
            }
            catch
            {
                // The native child may already be going away during window shutdown.
            }
        }
    }

    private string GetPresenterAdapterName()
    {
        var name = new StringBuilder(256);
        return U3DViewer_GetScenePresenterAdapterName(_hostWindow, name, name.Capacity) != 0
            ? name.ToString()
            : string.Empty;
    }

    private void SetStatus(string status)
    {
        if (string.Equals(_lastStatus, status, StringComparison.Ordinal))
        {
            return;
        }

        _lastStatus = status;
        StatusChanged?.Invoke(status);
    }

    private static string DescribeInitStage(int stage) => stage switch
    {
        1 => "FindAdapter",
        2 => "CreateDevice",
        3 => "QueryDevice1",
        4 => "OpenSharedResource",
        5 => "QueryKeyedMutex",
        6 => "CreateShaderResourceView",
        7 => "CreateShaders",
        8 => "QueryDxgiDevice",
        9 => "GetAdapter",
        10 => "GetFactory",
        11 => "CreateSwapChain",
        12 => "CreateRenderTarget",
        13 => "Ready",
        _ => "Unknown"
    };

    private static string FormatAdapter(string? name, ulong luid)
    {
        var displayName = string.IsNullOrWhiteSpace(name) ? "unknown GPU" : name.Trim();
        return luid == 0
            ? displayName
            : $"{displayName} [LUID 0x{luid:X16}]";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SceneInputState
    {
        public int RightMouse;
        public int Forward;
        public int Right;
        public int Up;
        public int Shift;
        public int FocusPressed;
        public int MouseDeltaX;
        public int MouseDeltaY;
        public int WheelDelta;
    }

    private sealed class SceneHostWindowHandle : PlatformHandle, INativeControlHostDestroyableControlHandle
    {
        public SceneHostWindowHandle(IntPtr handle)
            : base(handle, "HWND")
        {
        }

        public void Destroy()
        {
            U3DViewer_DestroySceneHostWindow(Handle);
        }
    }

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr U3DViewer_CreateSceneHostWindow(IntPtr parent);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void U3DViewer_DestroySceneHostWindow(IntPtr window);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int U3DViewer_OpenScenePresenter(IntPtr window, string sharedName, ulong adapterLuid);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void U3DViewer_CloseScenePresenter(IntPtr window);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int U3DViewer_PresentScene(IntPtr window);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int U3DViewer_PollSceneInput(IntPtr window, out SceneInputState output);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int U3DViewer_GetScenePresenterLastError(IntPtr window);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int U3DViewer_GetScenePresenterInitStage(IntPtr window);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong U3DViewer_GetScenePresenterAdapterLuid(IntPtr window);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int U3DViewer_GetScenePresenterAdapterName(IntPtr window, StringBuilder buffer, int capacity);
}
