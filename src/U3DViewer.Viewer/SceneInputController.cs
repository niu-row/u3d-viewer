using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using U3DViewer.Protocol;

namespace U3DViewer.Viewer;

internal sealed class SceneInputController : IDisposable
{
    private const float MouseSensitivity = 0.18f;
    private const float ShiftBoost = 4f;

    private readonly Control _surface;
    private readonly Action<string> _sendCommand;
    private readonly Action _focusSelected;
    private readonly HashSet<Key> _pressedKeys = new();
    private readonly DispatcherTimer _inputTimer = new();

    private bool _rightMouseLook;
    private Point _lastPointerPosition;
    private long _lastTickTimestamp;
    private float _moveSpeed = 10f;

    public SceneInputController(
        Control surface,
        Action<string> sendCommand,
        Action focusSelected)
    {
        _surface = surface;
        _sendCommand = sendCommand;
        _focusSelected = focusSelected;

        _surface.KeyDown += OnKeyDown;
        _surface.KeyUp += OnKeyUp;
        _surface.PointerPressed += OnPointerPressed;
        _surface.PointerReleased += OnPointerReleased;
        _surface.PointerMoved += OnPointerMoved;
        _surface.PointerWheelChanged += OnPointerWheelChanged;
        _surface.LostFocus += OnLostFocus;

        _inputTimer.Interval = TimeSpan.FromMilliseconds(16);
        _inputTimer.Tick += OnInputTick;
        _lastTickTimestamp = Stopwatch.GetTimestamp();
        _inputTimer.Start();
    }

    public void Dispose()
    {
        _inputTimer.Stop();
        _inputTimer.Tick -= OnInputTick;

        _surface.KeyDown -= OnKeyDown;
        _surface.KeyUp -= OnKeyUp;
        _surface.PointerPressed -= OnPointerPressed;
        _surface.PointerReleased -= OnPointerReleased;
        _surface.PointerMoved -= OnPointerMoved;
        _surface.PointerWheelChanged -= OnPointerWheelChanged;
        _surface.LostFocus -= OnLostFocus;

        _pressedKeys.Clear();
        _rightMouseLook = false;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F)
        {
            _focusSelected();
            e.Handled = true;
            return;
        }

        if (IsMovementKey(e.Key) || IsShiftKey(e.Key))
        {
            _pressedKeys.Add(e.Key);
            e.Handled = true;
            return;
        }

        var lookCommand = e.Key switch
        {
            Key.Left => ViewerCommandCodec.EncodeCameraLook(-4f, 0f),
            Key.Right => ViewerCommandCodec.EncodeCameraLook(4f, 0f),
            Key.Up => ViewerCommandCodec.EncodeCameraLook(0f, -4f),
            Key.Down => ViewerCommandCodec.EncodeCameraLook(0f, 4f),
            _ => null
        };

        if (lookCommand is not null)
        {
            _sendCommand(lookCommand);
            e.Handled = true;
        }
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (IsMovementKey(e.Key) || IsShiftKey(e.Key))
        {
            _pressedKeys.Remove(e.Key);
            e.Handled = true;
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _surface.Focus();
        var point = e.GetCurrentPoint(_surface);
        if (!point.Properties.IsRightButtonPressed)
        {
            return;
        }

        _rightMouseLook = true;
        _lastPointerPosition = e.GetPosition(_surface);
        e.Pointer.Capture(_surface);
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_rightMouseLook)
        {
            return;
        }

        var point = e.GetCurrentPoint(_surface);
        if (point.Properties.IsRightButtonPressed)
        {
            return;
        }

        StopMouseLook(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_rightMouseLook)
        {
            return;
        }

        var position = e.GetPosition(_surface);
        var deltaX = position.X - _lastPointerPosition.X;
        var deltaY = position.Y - _lastPointerPosition.Y;
        _lastPointerPosition = position;

        if (Math.Abs(deltaX) < 0.01 && Math.Abs(deltaY) < 0.01)
        {
            return;
        }

        _sendCommand(ViewerCommandCodec.EncodeCameraLook(
            (float)deltaX * MouseSensitivity,
            (float)deltaY * MouseSensitivity));
        e.Handled = true;
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var multiplier = Math.Pow(1.25, e.Delta.Y);
        _moveSpeed = Math.Clamp((float)(_moveSpeed * multiplier), 0.1f, 1000f);
        _sendCommand(ViewerCommandCodec.EncodeCameraSpeed(_moveSpeed));
        e.Handled = true;
    }

    private void OnLostFocus(object? sender, RoutedEventArgs e)
    {
        _pressedKeys.Clear();
        _rightMouseLook = false;
    }

    private void OnInputTick(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var deltaSeconds = (float)((now - _lastTickTimestamp) / (double)Stopwatch.Frequency);
        _lastTickTimestamp = now;
        deltaSeconds = Math.Clamp(deltaSeconds, 0f, 0.1f);

        if (!_rightMouseLook || deltaSeconds <= 0f)
        {
            return;
        }

        var forward = Axis(Key.W, Key.S);
        var right = Axis(Key.D, Key.A);
        var up = Axis(Key.E, Key.Q);
        var magnitude = MathF.Sqrt(forward * forward + right * right + up * up);
        if (magnitude <= 0f)
        {
            return;
        }

        if (magnitude > 1f)
        {
            forward /= magnitude;
            right /= magnitude;
            up /= magnitude;
        }

        if (_pressedKeys.Contains(Key.LeftShift) || _pressedKeys.Contains(Key.RightShift))
        {
            deltaSeconds *= ShiftBoost;
        }

        _sendCommand(ViewerCommandCodec.EncodeCameraMove(forward, right, up, deltaSeconds));
    }

    private float Axis(Key positive, Key negative)
    {
        var value = 0f;
        if (_pressedKeys.Contains(positive)) value += 1f;
        if (_pressedKeys.Contains(negative)) value -= 1f;
        return value;
    }

    private void StopMouseLook(IPointer pointer)
    {
        _rightMouseLook = false;
        _pressedKeys.Remove(Key.W);
        _pressedKeys.Remove(Key.A);
        _pressedKeys.Remove(Key.S);
        _pressedKeys.Remove(Key.D);
        _pressedKeys.Remove(Key.Q);
        _pressedKeys.Remove(Key.E);
        pointer.Capture(null);
    }

    private static bool IsMovementKey(Key key) =>
        key is Key.W or Key.A or Key.S or Key.D or Key.Q or Key.E;

    private static bool IsShiftKey(Key key) =>
        key is Key.LeftShift or Key.RightShift;
}
