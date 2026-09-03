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
    private readonly DispatcherTimer _inputTimer = new();
    private readonly DispatcherTimer _resizeRecoveryTimer = new();
    private readonly object _presenterStateLock = new();
    private readonly AutoResetEvent _presenterWake = new(false);
    private readonly ManualResetEventSlim _windowReleased = new(true);
    private readonly Thread _presenterThread;

    private IntPtr _hostWindow;
    private RenderTargetInfo? _target;
    private string _targetKey = string.Empty;
    private bool _resizePending;
    private int _presenterEpoch;
    private int _shutdownRequested;
    private long _lastTickTimestamp = Stopwatch.GetTimestamp();
    private float _moveSpeed = 10f;
    private string _lastStatus = string.Empty;
    private string _recoveryTargetKey = string.Empty;

    public NativeSceneHost(Action<string> sendCommand, Action focusSelected)
    {
        _sendCommand = sendCommand;
        _focusSelected = focusSelected;
        Focusable = true;

        _presenterThread = new Thread(PresenterLoop)
        {
            IsBackground = true,
            Name = "U3DViewer Scene Presenter"
        };
        _presenterThread.Start();

        _inputTimer.Interval = TimeSpan.FromMilliseconds(16);
        _inputTimer.Tick += OnInputTick;
        _inputTimer.Start();

        _resizeRecoveryTimer.Interval = TimeSpan.FromMilliseconds(300);
        _resizeRecoveryTimer.Tick += OnResizeRecoveryTick;
        SizeChanged += OnHostSizeChanged;
    }

    public event Action<string>? StatusChanged;
    public event Action<float>? MoveSpeedChanged;

    public float MoveSpeed => _moveSpeed;

    public void SetRenderTarget(RenderTargetInfo? target)
    {
        if (target is not null &&
            target.Available &&
            target.NativeBridgeAbiVersion != NativeBridgeProtocol.AbiVersion)
        {
            lock (_presenterStateLock)
            {
                _target = null;
                _targetKey = string.Empty;
                _presenterEpoch++;
            }

            _resizeRecoveryTimer.Stop();
            _presenterWake.Set();
            SetStatus(
                $"NativeBridge ABI mismatch: game={target.NativeBridgeAbiVersion}, viewer expects={NativeBridgeProtocol.AbiVersion}. " +
                "Redeploy U3DViewer.NativeBridge.dll and restart the game.");
            return;
        }

        if (target is null || !target.Available || string.IsNullOrWhiteSpace(target.SharedName))
        {
            lock (_presenterStateLock)
            {
                _target = null;
                _targetKey = string.Empty;
                _presenterEpoch++;
            }

            _resizeRecoveryTimer.Stop();
            _presenterWake.Set();
            SetStatus(target?.Status ?? "Waiting for the target game's Scene render target...");
            return;
        }

        if (target.MoveSpeed > 0f && Math.Abs(target.MoveSpeed - _moveSpeed) > 0.0001f)
        {
            _moveSpeed = Math.Clamp(target.MoveSpeed, 0.1f, 1000f);
            MoveSpeedChanged?.Invoke(_moveSpeed);
        }

        var key = $"{target.SharedName}|{target.AdapterLuid:X16}|{target.Width}x{target.Height}|{target.DxgiFormat}";
        lock (_presenterStateLock)
        {
            if (!string.Equals(_targetKey, key, StringComparison.Ordinal))
            {
                _targetKey = key;
                _presenterEpoch++;
            }
            _target = target;
        }

        _presenterWake.Set();
    }

    public void Shutdown()
    {
        _inputTimer.Stop();
        _resizeRecoveryTimer.Stop();

        lock (_presenterStateLock)
        {
            _target = null;
            _resizePending = false;
            _presenterEpoch++;
        }

        Interlocked.Exchange(ref _shutdownRequested, 1);
        _presenterWake.Set();
        if (_presenterThread.IsAlive)
        {
            _presenterThread.Join(TimeSpan.FromSeconds(1));
        }
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

        lock (_presenterStateLock)
        {
            _hostWindow = handle;
            _resizePending = false;
            _presenterEpoch++;
        }

        _windowReleased.Reset();
        _lastTickTimestamp = Stopwatch.GetTimestamp();
        _presenterWake.Set();
        return new SceneHostWindowHandle(handle);
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (control.Handle == GetHostWindow())
        {
            _resizeRecoveryTimer.Stop();
            lock (_presenterStateLock)
            {
                _hostWindow = IntPtr.Zero;
                _resizePending = false;
                _presenterEpoch++;
            }

            _windowReleased.Reset();
            _presenterWake.Set();
            if (_presenterThread.IsAlive)
            {
                _windowReleased.Wait(TimeSpan.FromSeconds(1));
            }
        }

        base.DestroyNativeControlCore(control);
    }

    private void OnHostSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width < 1 || e.NewSize.Height < 1 || GetHostWindow() == IntPtr.Zero)
        {
            return;
        }

        lock (_presenterStateLock)
        {
            _resizePending = true;
        }

        _presenterWake.Set();
        _resizeRecoveryTimer.Stop();
        _resizeRecoveryTimer.Start();
    }

    private void OnResizeRecoveryTick(object? sender, EventArgs e)
    {
        _resizeRecoveryTimer.Stop();

        lock (_presenterStateLock)
        {
            if (_hostWindow == IntPtr.Zero || _target is null)
            {
                _resizePending = false;
                return;
            }

            _resizePending = false;
            _presenterEpoch++;
        }

        _presenterWake.Set();
    }

    private void OnInputTick(object? sender, EventArgs e)
    {
        var nowTimestamp = Stopwatch.GetTimestamp();
        var deltaSeconds = (float)((nowTimestamp - _lastTickTimestamp) / (double)Stopwatch.Frequency);
        _lastTickTimestamp = nowTimestamp;
        deltaSeconds = Math.Clamp(deltaSeconds, 0f, 0.1f);

        var window = GetHostWindow();
        if (window != IntPtr.Zero)
        {
            PollInput(window, deltaSeconds);
        }
    }

    private void PresenterLoop()
    {
        IntPtr openedWindow = IntPtr.Zero;
        string openedKey = string.Empty;
        var openedEpoch = -1;
        var presenterOpen = false;
        var nextOpenAttemptUtc = DateTime.MinValue;

        try
        {
            while (Volatile.Read(ref _shutdownRequested) == 0)
            {
                _presenterWake.WaitOne(16);
                if (Volatile.Read(ref _shutdownRequested) != 0)
                {
                    break;
                }

                SnapshotPresenterState(
                    out var requestedWindow,
                    out var target,
                    out var requestedKey,
                    out var resizePending,
                    out var requestedEpoch);

                if (requestedWindow == IntPtr.Zero || target is null || resizePending)
                {
                    if (presenterOpen)
                    {
                        ClosePresenterNative(openedWindow);
                        presenterOpen = false;
                        openedWindow = IntPtr.Zero;
                        openedKey = string.Empty;
                        openedEpoch = -1;
                    }

                    if (requestedWindow == IntPtr.Zero)
                    {
                        _windowReleased.Set();
                    }
                    continue;
                }

                _windowReleased.Reset();

                if (presenterOpen &&
                    (openedWindow != requestedWindow ||
                     !string.Equals(openedKey, requestedKey, StringComparison.Ordinal) ||
                     openedEpoch != requestedEpoch))
                {
                    ClosePresenterNative(openedWindow);
                    presenterOpen = false;
                    openedWindow = IntPtr.Zero;
                    openedKey = string.Empty;
                    openedEpoch = -1;
                }

                if (!presenterOpen)
                {
                    var now = DateTime.UtcNow;
                    if (now < nextOpenAttemptUtc)
                    {
                        continue;
                    }

                    presenterOpen = TryOpenPresenterNative(requestedWindow, target, requestedKey);
                    if (!presenterOpen)
                    {
                        nextOpenAttemptUtc = now + TimeSpan.FromMilliseconds(250);
                        continue;
                    }

                    openedWindow = requestedWindow;
                    openedKey = requestedKey;
                    openedEpoch = requestedEpoch;
                    nextOpenAttemptUtc = DateTime.MinValue;
                }

                var presentResult = PresentNative(openedWindow);
                if (presentResult >= 0)
                {
                    if (presentResult > 0)
                    {
                        _recoveryTargetKey = string.Empty;
                    }
                    continue;
                }

                RequestRecoveryWatchdog("present failure", openedKey);
                ClosePresenterNative(openedWindow);
                presenterOpen = false;
                openedWindow = IntPtr.Zero;
                openedKey = string.Empty;
                openedEpoch = -1;
                nextOpenAttemptUtc = DateTime.UtcNow + TimeSpan.FromMilliseconds(250);
            }
        }
        finally
        {
            if (presenterOpen && openedWindow != IntPtr.Zero)
            {
                ClosePresenterNative(openedWindow);
            }
            _windowReleased.Set();
        }
    }

    private bool TryOpenPresenterNative(IntPtr window, RenderTargetInfo target, string targetKey)
    {
        try
        {
            var opened = U3DViewer_OpenScenePresenter(window, target.SharedName, target.AdapterLuid);
            var presenterLuid = U3DViewer_GetScenePresenterAdapterLuid(window);
            var presenterName = GetPresenterAdapterName(window);
            var gameGpu = FormatAdapter(target.AdapterName, target.AdapterLuid);
            var viewerGpu = FormatAdapter(presenterName, presenterLuid);

            if (opened == 0)
            {
                var hresult = U3DViewer_GetScenePresenterLastError(window);
                var initStage = U3DViewer_GetScenePresenterInitStage(window);
                var stage = DescribeInitStage(initStage);
                SetStatus(
                    $"Could not open GPU Scene presenter at {stage} (HRESULT 0x{hresult:X8}, source DXGI {target.DxgiFormat}). " +
                    $"Game GPU: {gameGpu} · Viewer GPU: {viewerGpu}");

                // A ready generation should normally open immediately. If it still fails, allow
                // one transport-only recovery for this exact generation, then wait for a new
                // generation instead of rebuilding the same transport once per second forever.
                if (initStage >= 4 && initStage <= 12)
                {
                    RequestRecoveryWatchdog($"open failure at {stage}", targetKey);
                }
                return false;
            }

            SetLiveStatus(target);
            ViewerLog.Info($"Scene zero-copy presenter opened. Game GPU: {gameGpu} · Viewer GPU: {viewerGpu}");
            return true;
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

        return false;
    }

    private int PresentNative(IntPtr window)
    {
        try
        {
            var result = U3DViewer_PresentScene(window);
            if (result >= 0)
            {
                return result;
            }

            var hresult = U3DViewer_GetScenePresenterLastError(window);
            SetStatus($"Scene GPU presentation failed (HRESULT 0x{hresult:X8}). Retrying...");
            ViewerLog.Error($"Scene GPU presentation failed (HRESULT 0x{hresult:X8}).");
        }
        catch (Exception ex)
        {
            SetStatus($"Scene GPU presentation failed: {ex.Message}. Retrying...");
            ViewerLog.Error("Scene GPU presentation threw an exception.", ex);
        }

        return -1;
    }

    private void RequestRecoveryWatchdog(string reason, string targetKey)
    {
        if (string.IsNullOrWhiteSpace(targetKey) ||
            string.Equals(_recoveryTargetKey, targetKey, StringComparison.Ordinal))
        {
            return;
        }

        _recoveryTargetKey = targetKey;
        ViewerLog.Warning($"Scene presenter watchdog requested transport recovery after {reason}.");
        Dispatcher.UIThread.Post(() => _sendCommand(ViewerCommandCodec.EncodeCameraRecover()));
    }

    private static void ClosePresenterNative(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            return;
        }

        try
        {
            U3DViewer_CloseScenePresenter(window);
        }
        catch
        {
            // The native child may already be going away during window shutdown.
        }
    }

    private void SnapshotPresenterState(
        out IntPtr window,
        out RenderTargetInfo? target,
        out string targetKey,
        out bool resizePending,
        out int epoch)
    {
        lock (_presenterStateLock)
        {
            window = _hostWindow;
            target = _target;
            targetKey = _targetKey;
            resizePending = _resizePending;
            epoch = _presenterEpoch;
        }
    }

    private IntPtr GetHostWindow()
    {
        lock (_presenterStateLock)
        {
            return _hostWindow;
        }
    }

    private void SetLiveStatus(RenderTargetInfo target)
    {
        SetStatus(
            $"LIVE · {target.Width}×{target.Height} · DXGI {target.DxgiFormat} · " +
            "RMB + mouse look · RMB + WASD/QE fly · Shift boost · wheel speed · F focus");
    }

    private void PollInput(IntPtr window, float deltaSeconds)
    {
        if (U3DViewer_PollSceneInput(window, out var input) == 0)
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

    private string GetPresenterAdapterName(IntPtr window)
    {
        var name = new StringBuilder(256);
        return U3DViewer_GetScenePresenterAdapterName(window, name, name.Capacity) != 0
            ? name.ToString()
            : string.Empty;
    }

    private void SetStatus(string status)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SetStatus(status));
            return;
        }

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
