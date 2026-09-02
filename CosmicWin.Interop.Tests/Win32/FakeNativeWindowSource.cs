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
    /// <remarks>
    /// A cloaked window keeps <c>WS_VISIBLE</c> -- it is still shown, just not on the desktop being
    /// looked at -- which is exactly what separates it from
    /// <see cref="SimulateWindowHiddenSilently"/>, so this leaves <c>IsVisible</c> alone.
    /// </remarks>
    public void HideFromEnumeration(nint hwnd) => _hiddenFromEnumeration.Add(hwnd);

    /// <summary>
    /// Simulates an application hiding its window into the notification area
    /// (<c>ShowWindow(SW_HIDE)</c>) AND the hook delivering the event for it. The window stays
    /// ALIVE -- the process is still running -- so it keeps answering
    /// <see cref="TryGetWindowInfo"/>; it simply stops being visible and stops being enumerable.
    /// </summary>
    public void SimulateWindowHiddenWithEvent(nint hwnd)
    {
        SimulateWindowHiddenSilently(hwnd);
        _callback?.Invoke(NativeWindowEventKind.Hidden, hwnd);
    }

    /// <summary>The same hide with the hook event dropped -- only a subsequent <c>Poll</c> can notice.</summary>
    public void SimulateWindowHiddenSilently(nint hwnd)
    {
        if (_windows.TryGetValue(hwnd, out var info))
        {
            _windows[hwnd] = info with { IsVisible = false };
        }

        _hiddenFromEnumeration.Add(hwnd);
    }

    public IReadOnlyList<nint> EnumerateTopLevelWindows() =>
        _windows.Keys.Where(handle => !_hiddenFromEnumeration.Contains(handle)).ToArray();

    public bool TryGetWindowInfo(nint hwnd, out NativeWindowInfo info) => _windows.TryGetValue(hwnd, out info);

    /// <summary>
    /// Where each handle is, as far as the shell is concerned. Absent means Windows will not say,
    /// which is the honest default: the real <c>IVirtualDesktopManager</c> declines for plenty of
    /// windows, and every test written before this question existed meant exactly that.
    /// </summary>
    private readonly Dictionary<nint, bool?> _onCurrentDesktop = new();

    public bool? IsOnCurrentDesktop(nint hwnd) => _onCurrentDesktop.GetValueOrDefault(hwnd);

    /// <summary>The window is cloaked but has not gone anywhere -- it is still filed under the desktop being looked at, which is what a dismissed window looks like.</summary>
    public void PlaceOnCurrentDesktop(nint hwnd) => _onCurrentDesktop[hwnd] = true;

    /// <summary>The window is cloaked because the user walked away from its desktop. It is alive and elsewhere.</summary>
    public void PlaceOnAnotherDesktop(nint hwnd) => _onCurrentDesktop[hwnd] = false;

    /// <summary>The shell declines to place it at all -- an <c>E_INVALIDARG</c> or an unreachable manager.</summary>
    public void RefuseToPlace(nint hwnd) => _onCurrentDesktop[hwnd] = null;

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

    private readonly Dictionary<nint, ActivationOutcome> _activationOutcomes = new();

    public ActivationOutcome Activate(nint hwnd)
    {
        _activationAttempts[hwnd] = _activationAttempts.GetValueOrDefault(hwnd) + 1;

        if (_failingActivationHandles.Contains(hwnd))
        {
            return ActivationOutcome.Failed;
        }

        return _activationOutcomes.GetValueOrDefault(hwnd, ActivationOutcome.Direct);
    }

    /// <summary>
    /// The boolean reading, for the facts that only care whether focus moved. Derived rather than
    /// answered separately, exactly as the real source derives it.
    /// </summary>
    public bool TryActivateWindow(nint hwnd) => Activate(hwnd).Confirmed();

    /// <summary>Handles that were asked to close, in order.</summary>
    public List<nint> CloseAsks { get; } = [];

    public bool TryClose(nint hwnd)
    {
        CloseAsks.Add(hwnd);
        return true;
    }

    /// <summary>
    /// Makes every subsequent <see cref="Activate"/> call for this handle report
    /// <paramref name="outcome"/> — the rung, not just success or refusal.
    /// </summary>
    public void ActivateAs(nint hwnd, ActivationOutcome outcome) => _activationOutcomes[hwnd] = outcome;

    /// <summary>Makes every subsequent <see cref="Activate"/> call for this handle fail.</summary>
    public void FailActivationFor(nint hwnd) => _failingActivationHandles.Add(hwnd);

    /// <summary>Number of times <see cref="Activate"/> was called for this handle.</summary>
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
        string className = "", string processName = "", uint style = 0u, uint exStyle = 0u, bool isOwned = false,
        bool isVisible = true) =>
        _windows[hwnd] = new NativeWindowInfo(
            title, bounds, className, processName, style, exStyle, isOwned, isVisible);

    /// <summary>Simulates the OS creating a window AND the hook delivering the event for it.</summary>
    public void SimulateWindowCreatedWithEvent(
        nint hwnd, string title, Rectangle bounds,
        string className = "", string processName = "", uint style = 0u, uint exStyle = 0u, bool isOwned = false)
    {
        _windows[hwnd] = new NativeWindowInfo(title, bounds, className, processName, style, exStyle, isOwned);

        // A re-shown window is enumerable again. Without this a test could never bring one back,
        // and coming back is precisely what a notification-area window does.
        _hiddenFromEnumeration.Remove(hwnd);
        _callback?.Invoke(NativeWindowEventKind.Created, hwnd);
    }

    /// <summary>Simulates the user grabbing the window (EVENT_SYSTEM_MOVESIZESTART).</summary>
    public void SimulateMoveSizeStart(nint hwnd) =>
        _callback?.Invoke(NativeWindowEventKind.MoveSizeStarted, hwnd);

    /// <summary>Simulates the user letting go (EVENT_SYSTEM_MOVESIZEEND).</summary>
    public void SimulateMoveSizeEnd(nint hwnd) =>
        _callback?.Invoke(NativeWindowEventKind.MoveSizeEnded, hwnd);

    /// <summary>Simulates the OS moving/resizing a window AND the hook delivering the event.</summary>
    /// <summary>
    /// Simulates DWM uncloaking a window that already existed -- returning to a virtual desktop.
    /// The window is NOT new: it is seeded first, exactly as a real one was there all along.
    /// </summary>
    public void SimulateWindowUncloakedWithEvent(nint hwnd, string title, Rectangle bounds)
    {
        if (!_windows.ContainsKey(hwnd))
        {
            SimulateWindowCreatedSilently(hwnd, title, bounds);
        }

        _hiddenFromEnumeration.Remove(hwnd);
        _callback?.Invoke(NativeWindowEventKind.Uncloaked, hwnd);
    }

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
