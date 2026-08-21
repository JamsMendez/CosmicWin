namespace CosmicWin.Interop;

/// <summary>
/// Represents a tracked top-level Win32 window. Mirrors the shape of winman's
/// <c>WinMan.IWindow</c> (see <c>fancywm/winman/src/WinMan/IWindow.cs</c>), trimmed to the
/// surface CosmicWin's tiling engine actually needs.
/// </summary>
/// <remarks>
/// Once a window dies (<see cref="IsAlive"/> becomes <c>false</c>), property reads MUST return
/// default-but-valid values (<see cref="string.Empty"/>, <see cref="Rectangle.Empty"/>) rather
/// than throw — this matches winman's documented contract and keeps callers (the tiling engine,
/// the hook-driven dispatcher) simple.
/// </remarks>
public interface IWindow : IEquatable<IWindow>
{
    /// <summary>
    /// The native window handle. Two <see cref="IWindow"/> instances that wrap the same handle
    /// MUST compare equal via <see cref="IEquatable{T}.Equals(T)"/>, regardless of any other
    /// property divergence (e.g. a stale title read before a change was observed).
    /// </summary>
    IntPtr Handle { get; }

    /// <summary>
    /// The window title. Returns <see cref="string.Empty"/> once <see cref="IsAlive"/> is <c>false</c>.
    /// </summary>
    string Title { get; }

    /// <summary>
    /// The window's client rectangle in real (unscaled) pixel coordinates.
    /// Returns <see cref="Rectangle.Empty"/> once <see cref="IsAlive"/> is <c>false</c>.
    /// </summary>
    Rectangle Bounds { get; }

    /// <summary>
    /// <c>false</c> once the underlying OS window has been destroyed or is otherwise no longer
    /// trackable.
    /// </summary>
    bool IsAlive { get; }

    /// <summary>
    /// <c>true</c> while this window can still be repositioned. Becomes <c>false</c> once a
    /// <see cref="SetPosition"/> call has failed — e.g. the window belongs to a
    /// higher-integrity/protected process this caller cannot touch (design threat-matrix row
    /// "Cross-process window manipulation"). Once <c>false</c>, callers (the tiling engine)
    /// should treat the window as untileable rather than retrying.
    /// </summary>
    bool CanReposition { get; }

    /// <summary>
    /// Repositions/resizes the window. Implementations MUST NOT throw for a window that refuses
    /// or fails the reposition (e.g. an elevated window this process cannot touch) — see design's
    /// threat-matrix row "Cross-process window manipulation". A failed reposition sets
    /// <see cref="CanReposition"/> to <c>false</c> instead of throwing or being retried in a loop.
    /// </summary>
    void SetPosition(Rectangle bounds);

    /// <summary>
    /// Brings this window to the foreground. Implementations MUST NOT throw when activation is
    /// refused or fails (e.g. a higher-integrity/protected window, or one owned by a process that
    /// currently blocks foreground changes) — returns <see langword="false"/> instead. Callers
    /// (the App-layer executor) MUST NOT retry on a returned <see langword="false"/>.
    /// </summary>
    bool TryActivate();

    /// <summary>Raw <c>GetClassName</c> value, uninterpreted by Interop (design D1/D7/D8 — only Layout's <c>WindowFilters</c> assigns meaning). Empty once dead.</summary>
    string ClassName { get; }

    /// <summary>Owning process executable file name (e.g. <c>"Spotify.exe"</c>, matching spec WE-2's config format). Empty once dead or unqueryable.</summary>
    string ProcessName { get; }

    /// <summary>Raw <c>GWL_STYLE</c> bits, uninterpreted (see <see cref="ClassName"/>).</summary>
    uint Style { get; }

    /// <summary>Raw <c>GWL_EXSTYLE</c> bits, uninterpreted (see <see cref="Style"/>).</summary>
    uint ExStyle { get; }

    /// <summary><c>true</c> if this window has an owner window — the raw signal WE-1's owned-dialog clause needs.</summary>
    bool IsOwned { get; }
}
