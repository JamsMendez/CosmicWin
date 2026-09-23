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
/// </remarks>
public static unsafe class PrimaryMonitorFullscreenDetector
{
    /// <summary>GWL_STYLE bits: <c>WS_CAPTION</c> (<c>WS_BORDER | WS_DLGFRAME</c>). Both bits must be set.</summary>
    private const uint StyleCaption = 0x00C00000;

    /// <summary>GWL_STYLE bit: <c>WS_MAXIMIZE</c>.</summary>
    private const uint StyleMaximized = 0x01000000;

    /// <summary>Same tolerance and rationale as a023fac's own constant: Chrome settles one pixel short.</summary>
    private const int FullscreenTolerance = 2;

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
}
