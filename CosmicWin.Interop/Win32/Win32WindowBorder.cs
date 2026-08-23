using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;

namespace CosmicWin.Interop.Win32;

/// <summary>
/// The accent border DWM draws around a window, recoloured per window.
/// </summary>
/// <remarks>
/// <para>
/// COLOUR ONLY. <c>DWMWA_VISIBLE_FRAME_BORDER_THICKNESS</c> is a GET -- DWM will say how many pixels
/// it draws and will not be told. There is no thickness attribute in the SDK's list of 29, so a
/// thicker border cannot be asked for and has to be drawn.
/// </para>
/// <para>
/// Whether this works at all on a window this process does not own is the open question, and it is
/// measured rather than assumed: <c>IVirtualDesktopManager.MoveWindowToDesktop</c> is documented,
/// takes any HWND, and still answers <c>E_ACCESSDENIED</c> for a window the caller does not own. A
/// window manager owns none of the windows it manages, so "documented" is not evidence here.
/// </para>
/// </remarks>
public static class Win32WindowBorder
{
    /// <summary>Let DWM pick, as if nothing had been set.</summary>
    public const uint Default = 0xFFFFFFFFu;

    /// <summary>Draw no border at all.</summary>
    public const uint None = 0xFFFFFFFEu;

    /// <summary>A <c>COLORREF</c>: 0x00BBGGRR, which is NOT the byte order of an HTML colour.</summary>
    public static uint ColorRef(byte red, byte green, byte blue) =>
        (uint)(red | (green << 8) | (blue << 16));

    /// <summary>
    /// Sets the border colour, returning the raw <c>HRESULT</c> so a refusal can be told apart from
    /// a success rather than swallowed into a bare <see langword="false"/>.
    /// </summary>
    public static unsafe int TrySetColor(nint hwnd, uint colorRef)
    {
        var value = colorRef;
        return PInvoke.DwmSetWindowAttribute(
            new HWND(hwnd),
            DWMWINDOWATTRIBUTE.DWMWA_BORDER_COLOR,
            &value,
            (uint)sizeof(uint));
    }
}
