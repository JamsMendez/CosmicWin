namespace CosmicWin.Layout.Filters;

/// <summary>
/// Win32 window style/extended-style bit values needed by <see cref="WindowFilters"/> (spec
/// WE-1). Declared as plain documented constants — not a Win32 P/Invoke type — so
/// <c>CosmicWin.Layout</c> stays Win32-free while still matching the real Windows
/// API bit values <c>CosmicWin.Interop</c> would read off a real <c>HWND</c>.
/// </summary>
public static class WindowStyleFlags
{
    /// <summary>GWL_EXSTYLE bit: <c>WS_EX_TOOLWINDOW</c>.</summary>
    public const uint ExToolWindow = 0x00000080;

    /// <summary>GWL_STYLE bit: <c>WS_MAXIMIZEBOX</c>.</summary>
    public const uint MaximizeBox = 0x00010000;

    /// <summary>GWL_STYLE bit: <c>WS_MINIMIZEBOX</c>.</summary>
    public const uint MinimizeBox = 0x00020000;

    /// <summary>GWL_STYLE bit: <c>WS_SYSMENU</c>.</summary>
    public const uint SystemMenu = 0x00080000;

    /// <summary>
    /// GWL_STYLE bit: <c>WS_THICKFRAME</c> (a.k.a. <c>WS_SIZEBOX</c>) -- the window carries a
    /// resize border, which is the window telling Windows that the user may resize it.
    /// </summary>
    /// <remarks>
    /// The most direct evidence a tiling manager can ask for, because resizing is the whole job. A
    /// window that invites the user to drag its edge cannot object to a tile doing the same thing,
    /// and the transient windows the exclusions target -- splashes, toasts, popups -- are
    /// fixed-size by construction.
    /// </remarks>
    public const uint ThickFrame = 0x00040000;

    /// <summary>
    /// GWL_STYLE bit: <c>WS_MAXIMIZE</c>. Like <see cref="Minimized"/> it is TRANSIENT -- Windows
    /// sets it while the window is maximised and clears it on restore. Unlike it, this bit is not
    /// an exclusion: a maximised window keeps its tile. It marks the one thing a maximised window
    /// is NOT doing, which is asking for a boundary between two tiles to move.
    /// </summary>
    public const uint Maximized = 0x01000000;

    /// <summary>
    /// GWL_STYLE bit: <c>WS_MINIMIZE</c> (a.k.a. <c>WS_ICONIC</c>). Set by Windows for as long as
    /// a window is minimized and cleared on restore -- unlike every other bit here it is
    /// TRANSIENT, which is why excluding on it has to be paired with re-admission on restore.
    /// </summary>
    public const uint Minimized = 0x20000000;
}
