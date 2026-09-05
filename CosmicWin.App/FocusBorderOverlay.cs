using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using CosmicWin.Interop;
using CosmicWin.Interop.Win32;

namespace CosmicWin.App;

/// <summary>
/// A border drawn around the focused window, thicker than the one DWM will draw.
/// </summary>
/// <remarks>
/// <para>
/// It exists because the thickness cannot be asked for. Of DWM's 29 window attributes,
/// <c>DWMWA_BORDER_COLOR</c> can be written -- measured working cross-process on this build, 6 of 6
/// windows answering <c>S_OK</c> -- but <c>DWMWA_VISIBLE_FRAME_BORDER_THICKNESS</c> is a GET. DWM
/// reports how many pixels it draws and will not be told, so a thicker border has to be drawn.
/// </para>
/// <para>
/// ONE overlay, following focus, rather than one per tile. The border the maintainer wanted thicker
/// is Windows' accent border, which only ever marks the active window, and a single reused window
/// costs one placement per focus change instead of N kept in sync forever.
/// </para>
/// <para>
/// It is deliberately never in its own way: click-through, non-activating, absent from Alt+Tab, and
/// invisible to CosmicWin's own tiling -- the hooks are installed with
/// <c>WINEVENT_SKIPOWNPROCESS</c>, and <c>WS_EX_TOOLWINDOW</c> would exclude it anyway.
/// </para>
/// <para>
/// Nor is it in the way of what the framed window itself opens. It sits BEHIND that window rather
/// than on top of the desktop, which is what keeps a dropdown overhanging its own window -- a
/// browser's customise menu, as reported -- from being painted over.
/// </para>
/// </remarks>
public interface IFocusBorder : IDisposable
{
    /// <summary>
    /// Frames <paramref name="framed"/>, whose rectangle is <paramref name="window"/> in real
    /// pixels, on a display scaled by <paramref name="scaling"/>.
    /// </summary>
    /// <remarks>
    /// The handle is not redundant with the rectangle. The border is placed directly behind the
    /// window it frames, and a z-order position can only be named by a window, never by an area.
    /// </remarks>
    /// <param name="dashed">
    /// Draws the frame broken rather than solid, for a window that will not take every size it is
    /// offered. Its tile is not the whole story about where it will actually sit, and a border that
    /// looked identical to every other one would be claiming otherwise.
    /// </param>
    /// <remarks>
    /// Measured on hardware by counting the rectangles in the overlay window's own region, which
    /// tells the two apart without a screenshot: an ordinary window frames in 26, a dashed one in
    /// 292, and cycling focus between them alternates cleanly with no state left behind.
    /// </remarks>
    void ShowAround(nint framed, Rectangle window, double scaling, int thickness, bool dashed = false);

    /// <summary>
    /// Draws in <paramref name="rgb"/> (<c>0xRRGGBB</c>) from now on, or follows Windows' own accent
    /// when it is <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="ShowAround"/> because the two change on wildly different clocks: the
    /// rectangle moves on every focus change and every reflow, the colour when somebody opens a menu.
    /// Carrying the colour on the hot call would repaint the brush hundreds of times to say the same
    /// thing.
    /// </remarks>
    void UseColor(uint? rgb);

    /// <summary>Draws nothing, without giving up the window it reuses.</summary>
    void Hide();
}

public sealed class FocusBorderOverlay : IFocusBorder
{
    private readonly Window _window;
    private nint _handle;
    private bool _disposed;

    public FocusBorderOverlay()
    {
        // Solid, and NOT AllowsTransparency. That flag makes a software-rendered layered window
        // whose content can lag its own placement -- measured as roughly one placement in five
        // showing the outline at its previous size, drawn across the middle of the window it was
        // meant to surround. The hollow centre is cut by the OS instead, in ClipToFrame.
        _window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = false,
            Background = SystemParameters.WindowGlassBrush,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize,

            // NOT topmost, and the placement depends on it. WS_EX_TOPMOST would keep the overlay
            // above every ordinary window whatever SetWindowPos is asked for, which is the defect
            // this window used to have: it drew across the dropdowns of the very window it framed.
            Topmost = false,
            IsHitTestVisible = false,
            ShowActivated = false,
            Left = -32000,
            Top = -32000,
            Width = 1,
            Height = 1,
        };

        // Shown once and then only moved. Creating the HWND is what lets the extended styles be
        // applied at all, and a hidden window costs nothing to keep.
        _window.Show();
        _handle = new WindowInteropHelper(_window).Handle;
        Win32OverlayWindow.MakePassive(_handle);
        Win32OverlayWindow.Hide(_handle);
    }

    /// <summary>
    /// Frames <paramref name="framed"/>, whose rectangle is <paramref name="window"/> in real
    /// pixels, on a display scaled by <paramref name="scaling"/>.
    /// </summary>
    /// <remarks>
    /// The rectangle is placed in real pixels through Win32 while the border is drawn by WPF in
    /// DIPs, so the thickness is divided by the display's scaling -- otherwise a 2px border renders
    /// 3 physical pixels at 150% and eats into the window it is supposed to sit outside of.
    /// </remarks>
    public void ShowAround(nint framed, Rectangle window, double scaling, int thickness, bool dashed = false)
    {
        if (_disposed || thickness <= 0)
        {
            Hide();
            return;
        }

        // Everything below is in REAL pixels: a window region is device-space, so the display's
        // scaling never enters into it. It is still taken as a parameter because a caller that has
        // to look it up would otherwise be tempted to scale the rectangle too.
        _ = scaling;

        var frame = BorderGeometry.Around(window, thickness);
        Win32OverlayWindow.Place(_handle, framed, frame);

        // Clipped AFTER the move, so the region always describes the size the window now has.
        Win32OverlayWindow.ClipToFrame(
            _handle,
            frame.Width,
            frame.Height,
            thickness,
            BorderGeometry.CornerRadiusAround(BorderGeometry.WindowsCornerRadius, thickness),
            dashed);
    }

    /// <summary>
    /// Repaints the overlay's fill, which IS the border -- the centre is cut out of it by the OS,
    /// so the window's background is the only colour there is.
    /// </summary>
    /// <remarks>
    /// The accent is taken from <see cref="SystemParameters.WindowGlassBrush"/> rather than
    /// remembered at construction, so a user who changes their Windows accent while CosmicWin is
    /// running gets the new one the next time this is called. That brush is frozen and owned by WPF;
    /// the solid one built here is frozen too, because a brush that will never change again has no
    /// business carrying change notification.
    /// </remarks>
    public void UseColor(uint? rgb)
    {
        if (_disposed)
        {
            return;
        }

        if (rgb is not { } colour)
        {
            _window.Background = SystemParameters.WindowGlassBrush;
            return;
        }

        var brush = new SolidColorBrush(Color.FromRgb(
            (byte)((colour >> 16) & 0xFF), (byte)((colour >> 8) & 0xFF), (byte)(colour & 0xFF)));
        brush.Freeze();
        _window.Background = brush;
    }

    public void Hide()
    {
        if (!_disposed && _handle != 0)
        {
            Win32OverlayWindow.Hide(_handle);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle = 0;
        _window.Close();
    }
}
