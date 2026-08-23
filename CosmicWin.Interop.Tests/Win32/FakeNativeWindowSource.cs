using CosmicWin.Interop;
using CosmicWin.Interop.Win32;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// In-memory <see cref="INativeWindowSource"/> that lets tests simulate the OS independently
/// from whether the hook actually fires — this is what makes the "hook misses an event, polling
/// catches it" scenario testable without a real desktop session.
/// </summary>
internal sealed class FakeNativeWindowSource : INativeWindowSource
{
    private readonly Dictionary<nint, NativeWindowInfo> _windows = new();
    private readonly HashSet<nint> _failingPositionHandles = new();
    private readonly Dictionary<nint, int> _setPositionAttempts = new();
    private readonly HashSet<nint> _failingActivationHandles = new();
    private readonly Dictionary<nint, int> _activationAttempts = new();
    private NativeWindowEventCallback? _callback;
    private Action<nint>? _shownCallback;

    private readonly HashSet<nint> _hiddenFromEnumeration = [];

    /// <summary>
    /// Simulates a window that still EXISTS but is no longer enumerable -- what DWM cloaking does
    /// to every window on a virtual desktop the user switches away from. TryGetWindowInfo keeps
    /// answering for it, because the window is alive; only the enumeration stops listing it.
    /// </summary>
    public void HideFromEnumeration(nint hwnd) => _hiddenFromEnumeration.Add(hwnd);

    public IReadOnlyList<nint> EnumerateTopLevelWindows() =>
        _windows.Keys.Where(handle => !_hiddenFromEnumeration.Contains(handle)).ToArray();

    public bool TryGetWindowInfo(nint hwnd, out NativeWindowInfo info) => _windows.TryGetValue(hwnd, out info);

    public bool SetWindowPosition(nint hwnd, Rectangle bounds)
    {
        _setPositionAttempts[hwnd] = _setPositionAttempts.GetValueOrDefault(hwnd) + 1;

        if (_failingPositionHandles.Contains(hwnd))
        {
            return false;
        }

        if (_windows.TryGetValue(hwnd, out var info))
        {
            _windows[hwnd] = info with { Bounds = bounds };
        }

        return true;
    }

    /// <summary>Makes every subsequent <see cref="SetWindowPosition"/> call for this handle fail.</summary>
    public void FailPositionFor(nint hwnd) => _failingPositionHandles.Add(hwnd);

    /// <summary>Number of times <see cref="SetWindowPosition"/> was called for this handle.</summary>
    public int SetPositionAttemptCount(nint hwnd) => _setPositionAttempts.GetValueOrDefault(hwnd);

    public bool TryActivateWindow(nint hwnd)
    {
        _activationAttempts[hwnd] = _activationAttempts.GetValueOrDefault(hwnd) + 1;
        return !_failingActivationHandles.Contains(hwnd);
    }

    /// <summary>Makes every subsequent <see cref="TryActivateWindow"/> call for this handle fail.</summary>
    public void FailActivationFor(nint hwnd) => _failingActivationHandles.Add(hwnd);

    /// <summary>Number of times <see cref="TryActivateWindow"/> was called for this handle.</summary>
    public int ActivationAttemptCount(nint hwnd) => _activationAttempts.GetValueOrDefault(hwnd);

    public IDisposable SubscribeWindowEvents(NativeWindowEventCallback callback)
    {
        _callback = callback;
        return new Subscription(this);
    }

    public IDisposable SubscribeShownWindows(Action<nint> callback)
    {
        _shownCallback = callback;
        return new ShownSubscription(this);
    }

    /// <summary>
    /// Simulates the ungated show hook firing. Deliberately independent of
    /// <see cref="SimulateWindowCreatedWithEvent"/>: the whole point of the second registration is
    /// that it reports windows the trackable path never delivers.
    /// </summary>
    public void SimulateWindowShown(nint hwnd) => _shownCallback?.Invoke(hwnd);

    /// <summary>Seeds a window as already open before <c>Open()</c> enumerates.</summary>
    public void SeedExistingWindow(
        nint hwnd, string title, Rectangle bounds,
        string className = "", string processName = "", uint style = 0u, uint exStyle = 0u, bool isOwned = false) =>
        _windows[hwnd] = new NativeWindowInfo(title, bounds, className, processName, style, exStyle, isOwned);

    /// <summary>Simulates the OS creating a window AND the hook delivering the event for it.</summary>
    public void SimulateWindowCreatedWithEvent(
        nint hwnd, string title, Rectangle bounds,
        string className = "", string processName = "", uint style = 0u, uint exStyle = 0u, bool isOwned = false)
    {
        _windows[hwnd] = new NativeWindowInfo(title, bounds, className, processName, style, exStyle, isOwned);
        _callback?.Invoke(NativeWindowEventKind.Created, hwnd);
    }

    /// <summary>Simulates the user grabbing the window (EVENT_SYSTEM_MOVESIZESTART).</summary>
    public void SimulateMoveSizeStart(nint hwnd) =>
        _callback?.Invoke(NativeWindowEventKind.MoveSizeStarted, hwnd);

    /// <summary>Simulates the user letting go (EVENT_SYSTEM_MOVESIZEEND).</summary>
    public void SimulateMoveSizeEnd(nint hwnd) =>
        _callback?.Invoke(NativeWindowEventKind.MoveSizeEnded, hwnd);

    /// <summary>Simulates the OS moving/resizing a window AND the hook delivering the event.</summary>
    public void SimulateWindowMovedWithEvent(nint hwnd, Rectangle newBounds)
    {
        if (_windows.TryGetValue(hwnd, out var info))
        {
            _windows[hwnd] = info with { Bounds = newBounds };
        }

        _callback?.Invoke(NativeWindowEventKind.BoundsChanged, hwnd);
    }

    /// <summary>Simulates the OS destroying a window AND the hook delivering the event for it.</summary>
    public void SimulateWindowDestroyedWithEvent(nint hwnd)
    {
        _windows.Remove(hwnd);
        _callback?.Invoke(NativeWindowEventKind.Destroyed, hwnd);
    }

    /// <summary>
    /// Simulates a window disappearing WITHOUT the hook delivering an event — the "hook misses
    /// an event" scenario. Only a subsequent <c>Poll</c> reconciliation will
    /// notice, via <see cref="EnumerateTopLevelWindows"/> no longer returning it.
    /// </summary>
    public void SimulateWindowDestroyedSilently(nint hwnd) => _windows.Remove(hwnd);

    /// <summary>
    /// Simulates a window appearing WITHOUT the hook delivering an event — the symmetric "hook
    /// misses a create" case, also only caught by a subsequent <c>Poll()</c>.
    /// </summary>
    public void SimulateWindowCreatedSilently(nint hwnd, string title, Rectangle bounds) =>
        _windows[hwnd] = new NativeWindowInfo(title, bounds);

    public void SimulateWindowChangedSilently(nint hwnd, string title, Rectangle bounds) =>
        _windows[hwnd] = new NativeWindowInfo(title, bounds);

    private sealed class Subscription : IDisposable
    {
        private readonly FakeNativeWindowSource _owner;

        public Subscription(FakeNativeWindowSource owner) => _owner = owner;

        public void Dispose() => _owner._callback = null;
    }

    private sealed class ShownSubscription : IDisposable
    {
        private readonly FakeNativeWindowSource _owner;

        public ShownSubscription(FakeNativeWindowSource owner) => _owner = owner;

        public void Dispose() => _owner._shownCallback = null;
    }
}
