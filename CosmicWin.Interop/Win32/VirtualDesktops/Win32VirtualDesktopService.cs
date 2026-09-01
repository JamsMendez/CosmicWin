namespace CosmicWin.Interop.Win32.VirtualDesktops;

/// <summary>
/// The native primitives <see cref="Win32VirtualDesktopService"/> needs, behind a seam so the
/// positional policy above them is unit-testable without a live shell — the same arrangement
/// <see cref="INativeWindowSource"/> gives <see cref="Win32Workspace"/>.
/// </summary>
internal interface INativeVirtualDesktops
{
    bool IsAvailable { get; }

    string? LastError { get; }

    IReadOnlyList<Guid> GetDesktopIds();

    Guid GetCurrentDesktopId();

    void CreateDesktop();

    void SwitchTo(Guid desktopId);

    bool MoveWindowTo(nint windowHandle, Guid desktopId);

    /// <summary>
    /// Closes the desktop currently in view. Returns nothing because there is nothing to return:
    /// this is Windows' own shortcut delivered as synthetic input, and the shell answers with an
    /// animation. The POLICY above decides whether it is worth asking; this only asks.
    /// </summary>
    void CloseCurrentDesktop();
}

/// <summary>
/// Positional virtual-desktop policy: index arithmetic, range limits and the create-on-demand rule,
/// all Win32-free and therefore testable against a fake.
/// </summary>
/// <remarks>
/// Every operation re-reads the desktop set instead of caching it. The user can add or remove
/// desktops at any moment through Task View or <c>Win+Ctrl+D</c>, and a cached index would then
/// address the WRONG desktop -- which, for a switch, silently takes the user somewhere they did not
/// ask to go. Reading is cheap; being wrong is not.
/// </remarks>
public sealed class Win32VirtualDesktopService : IVirtualDesktopService
{
    /// <summary>
    /// The highest index a chord can name (<c>Alt+1</c>..<c>Alt+9</c>). It doubles as a sanity
    /// bound: positional creation means a single mistyped index could otherwise ask the shell for
    /// an unbounded number of desktops.
    /// </summary>
    public const int MaxIndex = 9;

    /// <summary>
    /// How long to let a switch the shell has ACCEPTED become visible before calling it failed.
    /// </summary>
    /// <remarks>
    /// Twelve reads twenty milliseconds apart -- 240 ms at worst, and almost always one read. The
    /// budget is bounded by what a chord may block on, not by how slow an animation can get: this
    /// runs on the dispatcher pump, so a generous budget would stall every chord queued behind a
    /// switch the shell is never going to perform.
    /// </remarks>
    private const int SettleReads = 12;

    private static readonly TimeSpan SettleInterval = TimeSpan.FromMilliseconds(20);

    private readonly INativeVirtualDesktops _native;
    private readonly Action<TimeSpan> _settleWait;

    public Win32VirtualDesktopService()
        : this(new Win32NativeVirtualDesktops())
    {
    }

    /// <param name="settleWait">
    /// How to wait between verification reads. Injected so the settling facts cost no wall clock;
    /// production sleeps.
    /// </param>
    internal Win32VirtualDesktopService(INativeVirtualDesktops native, Action<TimeSpan>? settleWait = null)
    {
        _native = native;
        _settleWait = settleWait ?? Thread.Sleep;
    }

    public bool IsSupported => _native.IsAvailable;

    public string? LastError { get; private set; }

    public Guid CurrentDesktopId => _native.IsAvailable ? _native.GetCurrentDesktopId() : Guid.Empty;

    /// <summary>
    /// Which desktop a window lives on, or <see cref="Guid.Empty"/> when the shell will not say.
    /// Callers must treat empty as UNKNOWN and never as "the current one" -- filing a window under
    /// the wrong desktop puts it in a layout the user will meet unexpectedly later.
    /// </summary>
    public Guid ResolveWindowDesktop(nint windowHandle) =>
        Win32VirtualDesktopQueries.TryGetWindowDesktopId(windowHandle, out var id, out _) ? id : Guid.Empty;

    public int Count => _native.IsAvailable ? _native.GetDesktopIds().Count : 0;

    public int CurrentIndex
    {
        get
        {
            if (!_native.IsAvailable)
            {
                return 0;
            }

            // 0 when the current desktop is absent from the enumeration -- which would mean the two
            // disagree, and is exactly the "unknown" the contract documents.
            var ids = _native.GetDesktopIds();
            var current = _native.GetCurrentDesktopId();
            for (var index = 0; index < ids.Count; index++)
            {
                if (ids[index] == current)
                {
                    return index + 1;
                }
            }

            return 0;
        }
    }

    public bool TrySwitchTo(int oneBasedIndex)
    {
        LastError = null;
        if (!TryResolve(oneBasedIndex, out var desktopId))
        {
            LastError ??= _native.LastError;
            return false;
        }

        var switched = Switch(desktopId);
        LastError = switched ? null : _native.LastError;
        return switched;
    }

    public bool TryMoveWindowTo(nint windowHandle, int oneBasedIndex)
    {
        LastError = null;
        if (windowHandle == 0)
        {
            LastError = "No foreground window to move.";
            return false;
        }

        if (!TryResolve(oneBasedIndex, out var desktopId))
        {
            LastError ??= _native.LastError;
            return false;
        }

        var moved = _native.MoveWindowTo(windowHandle, desktopId);
        LastError = moved ? null : _native.LastError;
        return moved;
    }

    /// <summary>
    /// Closes the desktop in view, refusing the two cases that can be decided without the shell.
    /// </summary>
    /// <remarks>
    /// The LAST desktop is refused HERE rather than left to Windows, which ignores the request in
    /// silence. A chord that does nothing and says nothing is the exact failure <see cref="LastError"/>
    /// was added for after the first live run.
    /// </remarks>
    public bool TryCloseCurrentDesktop()
    {
        LastError = null;

        if (!_native.IsAvailable)
        {
            LastError = $"Unsupported build. {_native.LastError}".TrimEnd();
            return false;
        }

        // Read rather than remembered, like every other operation here: the user can close a desktop
        // through Task View between two chords, and a cached count would answer for a world that is
        // gone.
        var count = _native.GetDesktopIds().Count;
        if (count <= 1)
        {
            LastError = "The last desktop cannot be closed.";
            return false;
        }

        _native.CloseCurrentDesktop();
        return true;
    }

    private bool Switch(Guid desktopId)
    {
        // Already there: switching anyway costs the user a desktop-change animation for nothing.
        if (_native.GetCurrentDesktopId() == desktopId)
        {
            return true;
        }

        _native.SwitchTo(desktopId);

        // Verified, not assumed. SwitchDesktop returns void, so the only honest way to know whether
        // the user actually moved is to look -- the same lesson MR-2 taught about
        // SetForegroundWindow claiming success while nothing happened on screen.
        //
        // GIVEN TIME, because one immediate read of an ASYNCHRONOUS operation measures the shell's
        // reaction speed rather than its answer. A desktop change is animated, and a single read
        // taken the instant after asking reported the desktop the user was still leaving -- so a
        // switch that was really happening was declared failed, nothing retried, and the chord did
        // nothing at all in silence. Measured: `SwitchDesktop arg=2 ok=False index=1->1
        // error=(none)`, 18 ms after CosmicWin's own arriving-window redirect had moved the view,
        // with the identical chord succeeding 2.3 seconds later.
        //
        // Asked ONCE and then waited for. Re-issuing the switch on every read would fight an
        // animation already in flight, and the shell answers a second ask with a second desktop
        // change -- one chord, two moves.
        for (var read = 0; ; read++)
        {
            if (_native.GetCurrentDesktopId() == desktopId)
            {
                return true;
            }

            // The budget is spent. A shell that has not moved by now is refusing, and saying so is
            // better than blocking the chords queued behind this one any longer.
            if (read >= SettleReads)
            {
                return false;
            }

            _settleWait(SettleInterval);
        }
    }

    /// <summary>
    /// Resolves a one-based position to a desktop id, creating desktops until that position exists.
    /// </summary>
    private bool TryResolve(int oneBasedIndex, out Guid desktopId)
    {
        desktopId = Guid.Empty;
        if (!_native.IsAvailable)
        {
            LastError = $"Unsupported build. {_native.LastError}".TrimEnd();
            return false;
        }

        if (oneBasedIndex < 1 || oneBasedIndex > MaxIndex)
        {
            LastError = $"Index {oneBasedIndex} is outside 1..{MaxIndex}.";
            return false;
        }

        var ids = _native.GetDesktopIds();
        for (var guard = 0; ids.Count < oneBasedIndex && guard < MaxIndex; guard++)
        {
            _native.CreateDesktop();
            var grown = _native.GetDesktopIds();

            // A create that did not grow the set means the shell refused. Looping would spin
            // against a wall, so stop and report failure rather than hammering it.
            if (grown.Count <= ids.Count)
            {
                LastError = $"CreateDesktop did not grow the set (still {ids.Count}). {_native.LastError}".TrimEnd();
                return false;
            }

            ids = grown;
        }

        if (ids.Count < oneBasedIndex)
        {
            LastError = $"Only {ids.Count} desktop(s) exist after creating; wanted {oneBasedIndex}.";
            return false;
        }

        desktopId = ids[oneBasedIndex - 1];
        return true;
    }
}
