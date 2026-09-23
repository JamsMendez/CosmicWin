using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CosmicWin.Interop.Win32;

/// <summary>
/// T10 (live-alert-wallpaper): answers "is the desktop covered right now", for the alert overlay's
/// queue -- an alert must be HELD (not played out unseen) while a fullscreen window sits over the
/// primary monitor, per the maintainer's covered-desktop decision (see
/// <c>odd/tasks/live-alert-wallpaper.md</c>).
/// </summary>
/// <remarks>
/// <para>
/// Reuses, rather than re-invents, the fullscreen definition commit a023fac proved on hardware for
/// the tiling engine's own <c>MultiMonitorWorkspaceAdapter.IsFullscreen</c>: no caption, not
/// maximised, and covering the target rectangle to within <see cref="FullscreenTolerance"/> pixels
/// per edge (Chrome/Brave measured settling one pixel short). The two style-bit constants below are
/// numerically identical to <c>CosmicWin.Layout.Filters.WindowStyleFlags.Caption</c>/<c>Maximized</c>
/// on purpose, not a second, independently-chosen definition -- they are duplicated rather than
/// referenced because <c>CosmicWin.Interop</c> takes no project references at all (design D1/D8: it
/// is the leaf every other project depends on, so it cannot depend back on <c>CosmicWin.Layout</c> or
/// <c>CosmicWin.App</c>). <c>WindowStyleFlags</c> itself already documents this same cross-boundary
/// duplication for the identical reason.
/// </para>
/// <para>
/// Checked against the FOREGROUND window, not every top-level window: what actually hides the
/// desktop from the user is whatever they are looking at, and a fullscreen video or browser tab
/// takes the foreground the instant it goes fullscreen -- which is also exactly the shape of the T9
/// hardware probe this exists to fix (a borderless TOPMOST fullscreen form that had just taken
/// focus). Enumerating every top-level window instead would also flag a fullscreen-shaped window
/// sitting BEHIND something else, which is not actually covering anything.
/// </para>
/// <para>
/// R3 (<c>R3-desktop-shell-foreground-false-positive</c>): unlike the tiling rule above, which only
/// ever ran against filtered, tracked windows, this runs against WHATEVER is foreground -- and the
/// shell (<c>Progman</c>/<c>WorkerW</c>) becomes foreground on a plain desktop click or Win+D, a
/// caption-less popup spanning the whole monitor, exactly when the wallpaper is actually visible. A
/// click-through overlay (<c>WS_EX_TRANSPARENT</c> + <c>WS_EX_LAYERED</c>, e.g. the NVIDIA overlay's
/// "Press Alt+Z" toast) hides nothing underneath it either. See <see cref="IsExcludedFromCoverage"/>.
/// </para>
/// </remarks>
public static unsafe class PrimaryMonitorFullscreenDetector
{
    /// <summary>GWL_STYLE bits: <c>WS_CAPTION</c> (<c>WS_BORDER | WS_DLGFRAME</c>). Both bits must be set.</summary>
    private const uint StyleCaption = 0x00C00000;

    /// <summary>GWL_STYLE bit: <c>WS_MAXIMIZE</c>.</summary>
    private const uint StyleMaximized = 0x01000000;

    /// <summary>Same tolerance and rationale as a023fac's own constant: Chrome settles one pixel short.</summary>
    private const int FullscreenTolerance = 2;

    /// <summary>Desktop shell class names: <c>Progman</c>/<c>WorkerW</c> host the wallpaper, the two
    /// tray classes are the primary and secondary-monitor taskbars (for completeness).</summary>
    private const string ProgmanClassName = "Progman";
    private const string WorkerWClassName = "WorkerW";
    private const string TaskbarClassName = "Shell_TrayWnd";
    private const string SecondaryTaskbarClassName = "Shell_SecondaryTrayWnd";

    /// <summary>
    /// GWL_EXSTYLE bits: <c>WS_EX_TRANSPARENT | WS_EX_LAYERED</c>. Both must be set -- click-through
    /// alone does not prove the window paints no visible content of its own, but paired with
    /// <c>WS_EX_LAYERED</c> it is the shape of a compositor-drawn overlay toast.
    /// </summary>
    private const uint ClickThroughExStyle =
        (uint)WINDOW_EX_STYLE.WS_EX_TRANSPARENT | (uint)WINDOW_EX_STYLE.WS_EX_LAYERED;

    /// <summary>
    /// The real, Win32-backed answer: reads the current foreground window and the primary monitor,
    /// then applies <see cref="IsFullscreen"/>. Never throws -- a failed read (no foreground window,
    /// <c>GetMonitorInfo</c> failing) reads as "not covered" rather than crashing the 400ms watch
    /// tick that polls this.
    /// </summary>
    public static bool IsPrimaryMonitorCoveredByFullscreenWindow()
    {
        HWND foreground = PInvoke.GetForegroundWindow();
        if (foreground.IsNull)
        {
            return false;
        }

        bool isShellWindow = foreground == PInvoke.GetShellWindow();
        var exStyle = unchecked((uint)PInvoke.GetWindowLong(foreground, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE));
        string className = ReadClassName(foreground);

        if (IsExcludedFromCoverage(className, exStyle, isShellWindow))
        {
            return false;
        }

        HMONITOR primary = PInvoke.MonitorFromWindow(HWND.Null, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);
        MONITORINFO mi = new() { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!PInvoke.GetMonitorInfo(primary, ref mi))
        {
            return false;
        }

        if (!PInvoke.GetWindowRect(foreground, out RECT bounds))
        {
            return false;
        }

        var style = unchecked((uint)PInvoke.GetWindowLong(foreground, WINDOW_LONG_PTR_INDEX.GWL_STYLE));

        return IsFullscreen(
            style,
            new Rectangle(bounds.left, bounds.top, bounds.right, bounds.bottom),
            new Rectangle(mi.rcMonitor.left, mi.rcMonitor.top, mi.rcMonitor.right, mi.rcMonitor.bottom));
    }

    /// <summary>Raw <c>GetClassName</c> read. Degrades to empty rather than throwing on any failure.</summary>
    private static string ReadClassName(HWND hwnd)
    {
        Span<char> buffer = stackalloc char[256];
        int written;
        fixed (char* pBuffer = buffer)
        {
            written = PInvoke.GetClassName(hwnd, pBuffer, buffer.Length);
        }

        return written > 0 ? new string(buffer[..written]) : string.Empty;
    }

    /// <summary>
    /// The pure classification, exposed so it is unit-testable without a real desktop: no caption,
    /// not maximised, and <paramref name="bounds"/> covers <paramref name="monitor"/> to within
    /// <see cref="FullscreenTolerance"/> pixels per edge.
    /// </summary>
    internal static bool IsFullscreen(uint style, Rectangle bounds, Rectangle monitor)
    {
        if ((style & StyleCaption) == StyleCaption || (style & StyleMaximized) != 0)
        {
            return false;
        }

        return bounds.Left <= monitor.Left + FullscreenTolerance &&
               bounds.Top <= monitor.Top + FullscreenTolerance &&
               bounds.Right >= monitor.Right - FullscreenTolerance &&
               bounds.Bottom >= monitor.Bottom - FullscreenTolerance;
    }

    /// <summary>
    /// The pure exclusion, exposed so it is unit-testable without a real desktop: the foreground
    /// window never counts as covering the desktop when it IS the shell (<paramref
    /// name="isShellWindow"/>, or a known shell <paramref name="className"/>), or when
    /// <paramref name="exStyle"/> marks it a click-through overlay. See the class remarks for why.
    /// </summary>
    internal static bool IsExcludedFromCoverage(string className, uint exStyle, bool isShellWindow)
    {
        if (isShellWindow)
        {
            return true;
        }

        if (className is ProgmanClassName or WorkerWClassName or TaskbarClassName or SecondaryTaskbarClassName)
        {
            return true;
        }

        return (exStyle & ClickThroughExStyle) == ClickThroughExStyle;
    }
}
