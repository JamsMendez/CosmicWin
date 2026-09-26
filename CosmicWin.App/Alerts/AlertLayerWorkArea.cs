using CosmicWin.Interop;

namespace CosmicWin.App.Alerts;

/// <summary>
/// T7 (alert-tile-mosaic, 2026-09-26): the monitor's work area (<c>IDisplay.WorkArea</c>, read via
/// <c>Win32DisplayManager</c>/<c>GetMonitorInfo</c> -- see that class' remarks), expressed RELATIVE
/// to the alert layer's own surface -- the primary monitor's <c>IDisplay.Bounds</c>, which <see
/// cref="CosmicWin.Interop.Win32.Win32VideoWallpaperHost.CreateHostWindow"/>'s remarks establish the
/// host window is always created and positioned to exactly. No separate <c>GetWindowRect</c>/
/// <c>GetMonitorInfo</c> call is needed here: the display object <c>AppComposition</c> already
/// tracks (refreshed every watch tick, see <c>Win32DisplayManager.Refresh</c>) carries both
/// rectangles already. Physical pixels throughout -- the same unit <see
/// cref="AlertShowRequest.Gap"/> uses; the page (<c>alert-layer.js</c>) converts to CSS pixels with
/// the same devicePixelRatio-derived scale it already uses for the gap.
/// </summary>
/// <remarks>
/// Clamped to the surface, and collapsing to <see cref="Unavailable"/> (every field zero) when the
/// work area does not intersect the surface at all, or the surface itself is degenerate (zero or
/// negative size) -- a reading that should never happen on a real desktop, but must never take the
/// alert down if it somehow does. <see cref="Unavailable"/> is the same signal <c>alert-layer.js</c>
/// and <see cref="CosmicWin.App.Alerts.WebViewAlertLayerController"/> already treat as "lay the
/// mosaic out on the whole canvas instead", exactly the pre-T7 behaviour -- this alert must never
/// fail just because its work area could not be resolved.
/// </remarks>
public readonly record struct AlertLayerWorkArea(int Left, int Top, int Width, int Height)
{
    /// <summary>Signals "could not be read / does not fit the surface" -- see the type remarks.</summary>
    public static readonly AlertLayerWorkArea Unavailable = new(0, 0, 0, 0);

    /// <summary>
    /// Intersects <paramref name="workArea"/> with <paramref name="surfaceBounds"/> and expresses the
    /// result relative to <paramref name="surfaceBounds"/>'s own top-left corner -- never the
    /// desktop's -- so a monitor that does not start at (0,0) still gets a rect usable directly
    /// against the layer surface's own (0,0)-origin canvas.
    /// </summary>
    public static AlertLayerWorkArea Resolve(Rectangle surfaceBounds, Rectangle workArea)
    {
        if (surfaceBounds.Width <= 0 || surfaceBounds.Height <= 0)
        {
            return Unavailable;
        }

        var left = Math.Clamp(workArea.Left - surfaceBounds.Left, 0, surfaceBounds.Width);
        var top = Math.Clamp(workArea.Top - surfaceBounds.Top, 0, surfaceBounds.Height);
        var right = Math.Clamp(workArea.Right - surfaceBounds.Left, 0, surfaceBounds.Width);
        var bottom = Math.Clamp(workArea.Bottom - surfaceBounds.Top, 0, surfaceBounds.Height);

        return right > left && bottom > top
            ? new AlertLayerWorkArea(left, top, right - left, bottom - top)
            : Unavailable;
    }
}
