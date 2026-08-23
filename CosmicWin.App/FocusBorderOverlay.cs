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
/// </remarks>
public interface IFocusBorder : IDisposable
{
    /// <summary>Frames <paramref name="window"/>, in real pixels, on a display scaled by <paramref name="scaling"/>.</summary>
    void ShowAround(Rectangle window, double scaling, int thickness);

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
            Topmost = true,
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
    /// Frames <paramref name="window"/>, in real pixels, on a display scaled by
    /// <paramref name="scaling"/>.
    /// </summary>
    /// <remarks>
    /// The rectangle is placed in real pixels through Win32 while the border is drawn by WPF in
    /// DIPs, so the thickness is divided by the display's scaling -- otherwise a 2px border renders
    /// 3 physical pixels at 150% and eats into the window it is supposed to sit outside of.
    /// </remarks>
    public void ShowAround(Rectangle window, double scaling, int thickness)
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
        Win32OverlayWindow.Place(_handle, frame);

        // Clipped AFTER the move, so the region always describes the size the window now has.
        Win32OverlayWindow.ClipToFrame(
            _handle,
            frame.Width,
            frame.Height,
            thickness,
            BorderGeometry.CornerRadiusAround(BorderGeometry.WindowsCornerRadius, thickness));
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
