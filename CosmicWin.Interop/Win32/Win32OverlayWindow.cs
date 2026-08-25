using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CosmicWin.Interop.Win32;

/// <summary>
/// The Win32 side of an overlay this process owns: made click-through, and placed in REAL pixels.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not routed through <see cref="INativeWindowSource.SetWindowPosition"/>. That one
/// compensates for the invisible resize border so a TILE lands where the drawn frame should be,
/// which is exactly wrong here: an overlay has no such border and its rectangle is already the one
/// that must appear on screen.
/// </para>
/// <para>
/// It is deliberately NOT topmost. A topmost overlay outranks every ordinary window on the desktop,
/// including the dropdowns an application opens -- reported against a browser's customise menu,
/// which overhangs the window that opened it and had the border drawn across it. The overlay is
/// placed directly BELOW the window it frames instead: the ring lies entirely outside that window,
/// so being behind it hides nothing, and a popup always sits above its own owner and therefore above
/// the border too.
/// </para>
/// <para>
/// The extended styles are not decoration. Without <c>WS_EX_TRANSPARENT</c> the overlay swallows
/// every click aimed at the window it frames; without <c>WS_EX_NOACTIVATE</c> it steals the
/// foreground from that same window, and a window manager that fights its own focus is worse than
/// one with no border at all; without <c>WS_EX_TOOLWINDOW</c> it turns up in Alt+Tab.
/// </para>
/// </remarks>
public static class Win32OverlayWindow
{
    /// <summary>
    /// <c>GWL_EXSTYLE</c>, declared here rather than imported. CsWin32 projects the
    /// Get/SetWindowLongPtr pair inconsistently across architectures, and a hand-declared constant
    /// with the documented value cannot drift.
    /// </summary>
    private const int GwlExStyle = -20;

    /// <summary>
    /// <c>HWND_TOP</c>: the front of the ORDINARY band, above no topmost window at all. Used only
    /// when there is no window to sit behind, which in practice means a caller framing nothing.
    /// </summary>
    private static readonly HWND Top = HWND.Null;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetWindowLongPtrW(HWND hwnd, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowLongPtrW(HWND hwnd, int index, nint value);

    /// <summary>Makes the overlay invisible to the mouse, to activation and to Alt+Tab.</summary>
    public static void MakePassive(nint hwnd)
    {
        HWND handle = new(hwnd);
        var current = (uint)GetWindowLongPtrW(handle, GwlExStyle);

        SetWindowLongPtrW(
            handle,
            GwlExStyle,
            (nint)(current
                | (uint)WINDOW_EX_STYLE.WS_EX_TRANSPARENT
                | (uint)WINDOW_EX_STYLE.WS_EX_NOACTIVATE
                | (uint)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW));
    }

    /// <summary>
    /// Places the overlay at <paramref name="bounds"/> in real pixels, directly behind
    /// <paramref name="framed"/>, without taking the foreground or reordering anything else.
    /// </summary>
    /// <remarks>
    /// Re-asserted on every placement rather than set once, because the z-order moves underneath it:
    /// the window being framed comes forward every time it is activated, and an overlay left where
    /// it was would fall behind whatever the user clicked on next.
    /// </remarks>
    public static bool Place(nint hwnd, nint framed, Rectangle bounds) =>
        PInvoke.SetWindowPos(
            new HWND(hwnd),
            framed == 0 ? Top : new HWND(framed),
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);

    /// <summary>Hides the overlay without destroying it -- it is reused for the next focused window.</summary>
    public static bool Hide(nint hwnd) =>
        PInvoke.SetWindowPos(
            new HWND(hwnd),
            HWND.Null,
            0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE
            | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE
            | SET_WINDOW_POS_FLAGS.SWP_HIDEWINDOW);

    /// <summary>
    /// Clips the overlay to a hollow, round-cornered frame of <paramref name="thickness"/> pixels.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The hole is cut by the OS rather than painted transparent, and that is the whole point. Drawn
    /// as a WPF <c>AllowsTransparency</c> window it was a software-rendered layered window, and
    /// roughly one placement in five showed a stale frame -- the outline at its previous size, drawn
    /// across the middle of the window it was supposed to surround. A region has no frame to be
    /// stale: it is applied by the same call that moves the window.
    /// </para>
    /// <para>
    /// Coordinates are CLIENT-relative and in real pixels, so nothing here is scaled. The radii too,
    /// which is why this takes them already resolved rather than in DIPs.
    /// </para>
    /// </remarks>
    public static bool ClipToFrame(nint hwnd, int width, int height, int thickness, int outerRadius)
    {
        if (width <= 0 || height <= 0 || thickness <= 0)
        {
            return false;
        }

        // CreateRoundRectRgn takes the ELLIPSE's full width and height, not the radius, and its
        // bottom-right corner is exclusive -- both off-by-one traps that show as a clipped edge.
        var outer = PInvoke.CreateRoundRectRgn(0, 0, width + 1, height + 1, outerRadius * 2, outerRadius * 2);
        var innerRadius = Math.Max(0, outerRadius - thickness);
        var inner = PInvoke.CreateRoundRectRgn(
            thickness, thickness, width - thickness + 1, height - thickness + 1,
            innerRadius * 2, innerRadius * 2);

        try
        {
            if (outer.IsNull || inner.IsNull)
            {
                return false;
            }

            if (PInvoke.CombineRgn(outer, outer, inner, RGN_COMBINE_MODE.RGN_DIFF) == GDI_REGION_TYPE.RGN_ERROR)
            {
                return false;
            }

            // SetWindowRgn takes OWNERSHIP on success, so the outer region must not be deleted then.
            if (PInvoke.SetWindowRgn(new HWND(hwnd), outer, bRedraw: true) == 0)
            {
                return false;
            }

            outer = default;
            return true;
        }
        finally
        {
            if (!inner.IsNull)
            {
                PInvoke.DeleteObject(inner);
            }

            if (!outer.IsNull)
            {
                PInvoke.DeleteObject(outer);
            }
        }
    }
}
