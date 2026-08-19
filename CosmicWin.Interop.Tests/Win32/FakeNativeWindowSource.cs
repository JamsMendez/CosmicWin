using CosmicWin.Interop;
using CosmicWin.Interop.Win32;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// In-memory <see cref="INativeWindowSource"/> that lets tests simulate the OS independently
/// from whether the hook actually fires — this is what makes the "hook misses an event, polling
/// catches it" scenario (spec WT-1) testable without a real desktop session.
/// </summary>
internal sealed class FakeNativeWindowSource : INativeWindowSource
{
    private readonly Dictionary<nint, NativeWindowInfo> _windows = new();
    private readonly HashSet<nint> _failingPositionHandles = new();
    private readonly Dictionary<nint, int> _setPositionAttempts = new();
    private NativeWindowEventCallback? _callback;

    public IReadOnlyList<nint> EnumerateTopLevelWindows() => _windows.Keys.ToArray();

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

    public IDisposable SubscribeWindowEvents(NativeWindowEventCallback callback)
    {
        _callback = callback;
        return new Subscription(this);
    }

    /// <summary>Seeds a window as already open before <c>Open()</c> enumerates.</summary>
    public void SeedExistingWindow(nint hwnd, string title, Rectangle bounds) =>
        _windows[hwnd] = new NativeWindowInfo(title, bounds);

    /// <summary>Simulates the OS creating a window AND the hook delivering the event for it.</summary>
    public void SimulateWindowCreatedWithEvent(nint hwnd, string title, Rectangle bounds)
    {
        _windows[hwnd] = new NativeWindowInfo(title, bounds);
        _callback?.Invoke(NativeWindowEventKind.Created, hwnd);
    }

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
    /// an event" scenario (spec WT-1). Only a subsequent <c>Poll()</c> reconciliation will
    /// notice, via <see cref="EnumerateTopLevelWindows"/> no longer returning it.
    /// </summary>
    public void SimulateWindowDestroyedSilently(nint hwnd) => _windows.Remove(hwnd);

    /// <summary>
    /// Simulates a window appearing WITHOUT the hook delivering an event — the symmetric "hook
    /// misses a create" case, also only caught by a subsequent <c>Poll()</c>.
    /// </summary>
    public void SimulateWindowCreatedSilently(nint hwnd, string title, Rectangle bounds) =>
        _windows[hwnd] = new NativeWindowInfo(title, bounds);

    private sealed class Subscription : IDisposable
    {
        private readonly FakeNativeWindowSource _owner;

        public Subscription(FakeNativeWindowSource owner) => _owner = owner;

        public void Dispose() => _owner._callback = null;
    }
}
