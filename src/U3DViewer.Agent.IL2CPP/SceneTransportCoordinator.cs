namespace U3DViewer.Agent.IL2CPP;

internal enum SceneTransportOwner
{
    None = 0,
    FreeCamera = 1,
    DirectCapture = 2
}

/// <summary>
/// NativeBridge is process-global. Only one Scene producer may own its source texture at a time.
/// This coordinator prevents stale callbacks from one producer from resetting or publishing the
/// transport after ownership has moved to the other producer.
/// </summary>
internal static class SceneTransportCoordinator
{
    private static SceneTransportOwner _owner;
    private static int _epoch;

    internal static SceneTransportOwner Owner => _owner;
    internal static int Epoch => _epoch;

    internal static int Claim(SceneTransportOwner owner)
    {
        if (_owner != owner)
        {
            NativeBridge.U3DViewer_Reset();
            _owner = owner;
            _epoch++;
        }

        return _epoch;
    }

    internal static bool IsOwner(SceneTransportOwner owner) => _owner == owner;

    internal static void ResetIfOwner(SceneTransportOwner owner)
    {
        if (_owner != owner)
        {
            return;
        }

        NativeBridge.U3DViewer_Reset();
        _owner = SceneTransportOwner.None;
        _epoch++;
    }

    internal static void Release(SceneTransportOwner owner)
    {
        ResetIfOwner(owner);
    }
}
