namespace CosmicWin.Layout.Filters;

/// <summary>
/// Win32 window style/extended-style bit values needed by <see cref="WindowFilters"/> (spec
/// WE-1). Declared as plain documented constants — not a Win32 P/Invoke type — so
/// <c>CosmicWin.Layout</c> stays Win32-free (design D1/D8) while still matching the real Windows
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
    /// GWL_STYLE bit: <c>WS_MINIMIZE</c> (a.k.a. <c>WS_ICONIC</c>). Set by Windows for as long as
    /// a window is minimized and cleared on restore -- unlike every other bit here it is
    /// TRANSIENT, which is why excluding on it has to be paired with re-admission on restore.
    /// </summary>
    public const uint Minimized = 0x20000000;
}
