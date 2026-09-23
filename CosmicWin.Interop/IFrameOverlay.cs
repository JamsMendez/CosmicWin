using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;

namespace CosmicWin.Interop;

/// <summary>
/// Draws an optional per-frame overlay onto the video wallpaper back buffer after the video frame is
/// transferred and before the swapchain is presented.
/// </summary>
/// <remarks>
/// Public root Interop seam so later application wiring can pass an overlay implementation without
/// exposing Win32 texture types outside this assembly. The draw method stays internal for the same
/// reason <see cref="IVideoWallpaperHost.Device"/> and <see cref="IVideoWallpaperHost.GetBackBuffer"/>
/// do: only Interop should touch the CsWin32 D3D surface.
/// </remarks>
public interface IFrameOverlay
{
    /// <summary>Draws onto <paramref name="backBuffer"/> inside <paramref name="destination"/>.</summary>
    internal void Draw(ID3D11Texture2D backBuffer, RECT destination);
}
