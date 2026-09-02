namespace CosmicWin.Interop.Win32;

/// <summary>
/// WT-1: enumerates top-level windows at startup and tracks create/destroy/move/resize via a
/// native event source (<c>SetWinEventHook</c> for <see cref="Win32NativeWindowSource"/>), with
/// <see cref="Poll"/> as the bounded-interval reconciliation fallback for a missed event.
/// </summary>
/// <remarks>
/// Enumerate, hook, then dirty-check: the three together are scoped to what WT-1
/// needs. The actual native calls are behind
/// <see cref="INativeWindowSource"/> so the tracking algorithm is unit-testable without a real
/// desktop session; production callers use the parameterless constructor, which wires up the
/// real <see cref="Win32NativeWindowSource"/>.
/// </remarks>
public sealed class Win32Workspace : IWorkspace
{
    private readonly INativeWindowSource _nativeSource;
    private readonly Dictionary<nint, Win32Window> _windows = new();

    /// <summary>
    /// Windows the user is currently dragging or resizing by hand. Every bounds change between
    /// MOVESIZESTART and MOVESIZEEND is an intermediate frame of one gesture, so reporting them
    /// makes every listener answer the drag mid-flight -- measured as a window that flickers under
    /// the cursor and refuses to move, because an earlier decision's snap-back re-applied its tile on each
    /// one. The cached bounds are deliberately left stale for the duration, which is what lets the
    /// drop fire exactly one event carrying the settled position.
    /// </summary>
    private readonly HashSet<nint> _beingDragged = [];

    /// <summary>
    /// Handles that ONE reconciliation pass found cloaked while still filed under the desktop being
    /// looked at -- the shape of a dismissal. They are not removed on that reading, because a
    /// desktop switch cloaks its windows and moves the current desktop as two separate steps, and a
    /// pass landing between them sees exactly this. Requiring the fact twice running costs one
    /// interval and makes the transient case unreachable; see <see cref="Poll"/>.
    /// </summary>
    private readonly HashSet<nint> _lookedDismissedLastPass = [];
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
            TryAddWindow(hwnd, WindowArrival.Adopted);
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
            if (currentSet.Contains(hwnd))
            {
                _lookedDismissedLastPass.Remove(hwnd);
                continue;
            }

            // Absent from the enumeration is NOT the same as gone. IsTrackable rejects a cloaked
            // window, and DWM cloaks every window on a virtual desktop the user switches away from
            // -- so treating absence as destruction dismantled the whole layout on a desktop change
            // and rebuilt it in enumeration order on the way back (measured). A window
            // that still answers TryGetWindowInfo is alive; only its visibility changed.
            //
            // Which is why alive is not the whole test either. An application that lives in the
            // notification area does not destroy its window when the user closes it, it HIDES it,
            // and a hidden window answers every liveness question for as long as the process runs
            // -- so this pass kept handing it a tile forever (reported with Discord: the slot it
            // left behind was never reclaimed). WS_VISIBLE is what tells the two apart: cloaking
            // leaves it set, ShowWindow(SW_HIDE) clears it.
            if (!_nativeSource.TryGetWindowInfo(hwnd, out var info) || !info.IsVisible)
            {
                RemoveWindow(hwnd);
                continue;
            }

            // Alive, still WS_VISIBLE, and gone from the enumeration: cloaked. Which of the two
            // cloaks it is decides everything above, and neither test already made can tell them
            // apart -- an application cloaks its own window to DISMISS it, keeping WS_VISIBLE set
            // exactly like the desktop switch does. Reported with the Windows emoji panel, which
            // Chrome opens from its context menu: it took a tile, was dismissed, and held that tile
            // for the rest of the session, drawing a focus border on nothing every time the user
            // walked back to the desktop.
            //
            // The desktop is the discriminator. A window cloaked while STILL filed under the
            // desktop being looked at did not go anywhere -- there is nowhere else it could be.
            //
            // Asked through the DOCUMENTED IVirtualDesktopManager, and answered three ways: a
            // refusal is null and is NOT a "no". Reading it as one would report a living window as
            // closed on the strength of an error, so an unplaceable window keeps its tile.
            if (_nativeSource.IsOnCurrentDesktop(hwnd) != true)
            {
                _lookedDismissedLastPass.Remove(hwnd);
                continue;
            }

            // Twice running, and only twice running. Switching desktops cloaks the departing
            // windows and moves the current desktop as two separate steps; a pass landing between
            // them reads a still-current desktop for a window that is on its way out, which is a
            // dismissal to the letter. Acting on that single reading would put back the regression
            // that dismantled the whole tree on a desktop switch -- for a tile reclaimed one
            // interval sooner. A switch settles well inside one interval; a dismissed window
            // answers this way for as long as it exists.
            if (!_lookedDismissedLastPass.Add(hwnd))
            {
                RemoveWindow(hwnd);
            }
        }

        foreach (var hwnd in current)
        {
            if (_windows.ContainsKey(hwnd))
            {
                // The reconciliation pass answers a MISSED event, not a gesture in flight: reporting
                // a half-finished drag here would reintroduce the fight the drag bracket removes.
                if (!_beingDragged.Contains(hwnd))
                {
                    UpdateBounds(hwnd);
                }
            }
            else
            {
                TryAddWindow(hwnd, WindowArrival.Adopted);
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
                TryAddWindow(hwnd, WindowArrival.Created);
                break;

            // The window was already there; the user walked back to the desktop it lives on.
            // Tracked exactly like a create -- it must join the tree either way -- and reported as
            // the ADOPTION it is, so nothing downstream mistakes returning to a desktop for nine
            // windows being born on it.
            case NativeWindowEventKind.Uncloaked:
                TryAddWindow(hwnd, WindowArrival.Adopted);
                break;
            case NativeWindowEventKind.Destroyed:

            // Tracked exactly like a destroy, on purpose. The two are different facts about the
            // window -- a hidden one still has a live HWND and comes back through Created when the
            // user reopens it from the tray -- but they are the SAME fact about the layout: nothing
            // is drawn into that tile any more, so nothing may keep claiming it.
            case NativeWindowEventKind.Hidden:
                RemoveWindow(hwnd);
                break;
            case NativeWindowEventKind.BoundsChanged:
                if (!_beingDragged.Contains(hwnd))
                {
                    UpdateBounds(hwnd);
                }

                break;
            case NativeWindowEventKind.MoveSizeStarted:
                _beingDragged.Add(hwnd);
                break;
            case NativeWindowEventKind.MoveSizeEnded:
                if (_beingDragged.Remove(hwnd))
                {
                    // The ONE bounds change the user performed on purpose. Flagged as such because
                    // the drop is indistinguishable from every other move once it is a rectangle,
                    // and only this one may be read as a request to change the layout.
                    UpdateBounds(hwnd, isUserGesture: true);
                }

                break;
        }
    }

    /// <param name="arrival">
    /// Whether this announcement is a birth or an adoption. The enumeration paths adopt: they
    /// report windows that already existed and were merely not being tracked yet -- at startup, or
    /// because they were cloaked on a desktop the user had not visited. Only the shell's own
    /// creation event is a birth, and only a birth carries a placement decision worth overruling.
    /// </param>
    private void TryAddWindow(nint hwnd, WindowArrival arrival)
    {
        if (_windows.ContainsKey(hwnd))
        {
            return;
        }

        if (!_nativeSource.TryGetWindowInfo(hwnd, out var info))
        {
            return;
        }

        var window = new Win32Window(
            hwnd, info.Title, info.Bounds, _nativeSource,
            info.ClassName, info.ProcessName, info.Style, info.ExStyle, info.IsOwned);
        _windows[hwnd] = window;
        WindowAdded?.Invoke(this, new WindowEventArgs(window, arrival: arrival));
    }

    private void RemoveWindow(nint hwnd)
    {
        // Clear the drag flag first: a window destroyed mid-gesture never gets its MOVESIZEEND, and
        // a stale entry would silently withhold bounds events from its handle for the rest of the
        // session if the OS ever reused it.
        _beingDragged.Remove(hwnd);

        // Same reason, and the same reuse hazard: a handle left here would count as one pass
        // already served against whatever window Windows hands the value to next.
        _lookedDismissedLastPass.Remove(hwnd);

        if (!_windows.Remove(hwnd, out var window))
        {
            return;
        }

        window.MarkDead();
        WindowRemoved?.Invoke(this, new WindowEventArgs(window));
    }

    private void UpdateBounds(nint hwnd, bool isUserGesture = false)
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

        var boundsChanged = window.Bounds != info.Bounds;
        if (window.Title != info.Title || boundsChanged)
        {
            window.Refresh(info.Title, info.Bounds, info.ClassName, info.ProcessName, info.Style, info.ExStyle, info.IsOwned);
        }

        if (boundsChanged)
        {
            WindowBoundsChanged?.Invoke(this, new WindowEventArgs(window, isUserGesture));
        }
    }

    private void CheckOpen()
    {
        if (!IsOpen)
        {
            throw new InvalidOperationException("Call Open() first.");
        }
    }
}
