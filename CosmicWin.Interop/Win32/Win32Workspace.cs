namespace CosmicWin.Interop.Win32;

/// <summary>
/// WT-1: enumerates top-level windows at startup and tracks create/destroy/move/resize via a
/// native event source (<c>SetWinEventHook</c> for <see cref="Win32NativeWindowSource"/>), with
/// <see cref="Poll"/> as the bounded-interval reconciliation fallback for a missed event.
/// </summary>
/// <remarks>
/// Ports the enumerate + hook + dirty-check pattern from fancywm's
/// <c>WinMan.Windows.Win32Workspace</c>/<c>WinEventHookHelper</c> (algorithm only — this is
/// original code, not a copy), scoped to what WT-1 needs. The actual native calls are behind
/// <see cref="INativeWindowSource"/> so the tracking algorithm is unit-testable without a real
/// desktop session; production callers use the parameterless constructor, which wires up the
/// real <see cref="Win32NativeWindowSource"/>.
/// </remarks>
public sealed class Win32Workspace : IWorkspace
{
    private readonly INativeWindowSource _nativeSource;
    private readonly Dictionary<nint, TrackedWindow> _windows = new();
    private IDisposable? _hookSubscription;

    public event EventHandler<WindowEventArgs>? WindowAdded;
    public event EventHandler<WindowEventArgs>? WindowRemoved;
    public event EventHandler<WindowEventArgs>? WindowBoundsChanged;

    public bool IsOpen { get; private set; }

    public IReadOnlyList<IWindow> Snapshot => _windows.Values.Cast<IWindow>().ToArray();

    public Win32Workspace()
        : this(new Win32NativeWindowSource())
    {
    }

    internal Win32Workspace(INativeWindowSource nativeSource)
    {
        _nativeSource = nativeSource;
    }

    public void Open()
    {
        if (IsOpen)
        {
            throw new InvalidOperationException("Workspace has already been Open()ed.");
        }

        foreach (var hwnd in _nativeSource.EnumerateTopLevelWindows())
        {
            TryAddWindow(hwnd);
        }

        _hookSubscription = _nativeSource.SubscribeWindowEvents(OnNativeWindowEvent);
        IsOpen = true;
    }

    public void Poll()
    {
        CheckOpen();

        var current = _nativeSource.EnumerateTopLevelWindows();
        var currentSet = new HashSet<nint>(current);

        foreach (var hwnd in _windows.Keys.ToArray())
        {
            if (!currentSet.Contains(hwnd))
            {
                RemoveWindow(hwnd);
            }
        }

        foreach (var hwnd in current)
        {
            if (!_windows.ContainsKey(hwnd))
            {
                TryAddWindow(hwnd);
            }
        }
    }

    public void Dispose()
    {
        _hookSubscription?.Dispose();
        _hookSubscription = null;
    }

    private void OnNativeWindowEvent(NativeWindowEventKind kind, nint hwnd)
    {
        switch (kind)
        {
            case NativeWindowEventKind.Created:
                TryAddWindow(hwnd);
                break;
            case NativeWindowEventKind.Destroyed:
                RemoveWindow(hwnd);
                break;
            case NativeWindowEventKind.BoundsChanged:
                UpdateBounds(hwnd);
                break;
        }
    }

    private void TryAddWindow(nint hwnd)
    {
        if (_windows.ContainsKey(hwnd))
        {
            return;
        }

        if (!_nativeSource.TryGetWindowInfo(hwnd, out var info))
        {
            return;
        }

        var window = new TrackedWindow(hwnd, info.Title, info.Bounds, _nativeSource);
        _windows[hwnd] = window;
        WindowAdded?.Invoke(this, new WindowEventArgs(window));
    }

    private void RemoveWindow(nint hwnd)
    {
        if (!_windows.Remove(hwnd, out var window))
        {
            return;
        }

        window.MarkDead();
        WindowRemoved?.Invoke(this, new WindowEventArgs(window));
    }

    private void UpdateBounds(nint hwnd)
    {
        if (!_windows.TryGetValue(hwnd, out var window))
        {
            return;
        }

        if (!_nativeSource.TryGetWindowInfo(hwnd, out var info))
        {
            RemoveWindow(hwnd);
            return;
        }

        window.Refresh(info.Title, info.Bounds);
        WindowBoundsChanged?.Invoke(this, new WindowEventArgs(window));
    }

    private void CheckOpen()
    {
        if (!IsOpen)
        {
            throw new InvalidOperationException("Call Open() first.");
        }
    }
}
