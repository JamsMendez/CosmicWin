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

    private readonly INativeVirtualDesktops _native;

    public Win32VirtualDesktopService()
        : this(new Win32NativeVirtualDesktops())
    {
    }

    internal Win32VirtualDesktopService(INativeVirtualDesktops native)
    {
        _native = native;
    }

    public bool IsSupported => _native.IsAvailable;

    public string? LastError { get; private set; }

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
        return _native.GetCurrentDesktopId() == desktopId;
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
