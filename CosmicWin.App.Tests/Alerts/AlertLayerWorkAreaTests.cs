using CosmicWin.App.Alerts;
using CosmicWin.Interop;

namespace CosmicWin.App.Tests.Alerts;

/// <summary>
/// T7 (alert-tile-mosaic, 2026-09-26): <see cref="AlertLayerWorkArea.Resolve"/> is the PURE
/// arithmetic behind "lay the N&gt;1 mosaic out inside the work area" -- turns the monitor's own
/// work area (<c>IDisplay.WorkArea</c>) into a rect RELATIVE to the alert layer's surface (the
/// primary monitor's own bounds, <c>IDisplay.Bounds</c> -- see <c>Win32VideoWallpaperHost.
/// CreateHostWindow</c>'s remarks for why the host window's screen rect always equals the monitor's
/// bounds), clamped to it. Unit-tested directly, with no live desktop, mirroring
/// <c>AlertTileLayout</c>'s own split between pure layout math and the composition/controller that
/// carries it end to end (<c>WebViewAlertCompositionWiringTests</c>).
/// </summary>
public sealed class AlertLayerWorkAreaTests
{
    [Fact]
    public void NoTaskbar_WorkAreaEqualsTheWholeSurface()
    {
        var bounds = Rectangle.FromSize(0, 0, 1920, 1080);
        var workArea = Rectangle.FromSize(0, 0, 1920, 1080);

        var resolved = AlertLayerWorkArea.Resolve(bounds, workArea);

        Assert.Equal(new AlertLayerWorkArea(0, 0, 1920, 1080), resolved);
    }

    [Fact]
    public void TaskbarDockedRight_NarrowsTheRelativeWidthOnly()
    {
        var bounds = Rectangle.FromSize(0, 0, 1920, 1080);
        var workArea = Rectangle.FromSize(0, 0, 1720, 1080);

        var resolved = AlertLayerWorkArea.Resolve(bounds, workArea);

        Assert.Equal(new AlertLayerWorkArea(0, 0, 1720, 1080), resolved);
    }

    /// <summary>
    /// The monitor is not at the desktop origin (a secondary-looking layout, or any monitor whose
    /// bounds do not start at 0,0) -- the RELATIVE rect must be expressed against the surface's own
    /// top-left, never the desktop's, or a mosaic on a non-origin monitor would offset every tile.
    /// </summary>
    [Fact]
    public void MonitorNotAtTheDesktopOrigin_IsExpressedRelativeToTheSurfaceNotTheDesktop()
    {
        var bounds = Rectangle.FromSize(100, 50, 1920, 1080);
        // Taskbar on top, 40px tall.
        var workArea = Rectangle.FromSize(100, 90, 1920, 1040);

        var resolved = AlertLayerWorkArea.Resolve(bounds, workArea);

        Assert.Equal(new AlertLayerWorkArea(0, 40, 1920, 1040), resolved);
    }

    /// <summary>A work area extending past the surface on either edge is clamped to it, never overhangs.</summary>
    [Fact]
    public void WorkAreaWiderThanTheSurface_IsClampedToIt()
    {
        var bounds = Rectangle.FromSize(0, 0, 1920, 1080);
        var workArea = Rectangle.FromSize(-50, -20, 2200, 1300);

        var resolved = AlertLayerWorkArea.Resolve(bounds, workArea);

        Assert.Equal(new AlertLayerWorkArea(0, 0, 1920, 1080), resolved);
    }

    /// <summary>No overlap at all (e.g. a stale reading from a different monitor) falls back to <see cref="AlertLayerWorkArea.Unavailable"/>, never a negative or inverted rect.</summary>
    [Fact]
    public void WorkAreaOutsideTheSurface_FallsBackToUnavailable()
    {
        var bounds = Rectangle.FromSize(0, 0, 1920, 1080);
        var workArea = Rectangle.FromSize(3000, 3000, 1920, 1080);

        var resolved = AlertLayerWorkArea.Resolve(bounds, workArea);

        Assert.Equal(AlertLayerWorkArea.Unavailable, resolved);
    }

    /// <summary>A degenerate (zero/negative-size) surface can never be laid out on -- falls back rather than dividing by nothing downstream.</summary>
    [Fact]
    public void DegenerateSurface_FallsBackToUnavailable()
    {
        var bounds = Rectangle.FromSize(0, 0, 0, 0);
        var workArea = Rectangle.FromSize(0, 0, 1920, 1080);

        var resolved = AlertLayerWorkArea.Resolve(bounds, workArea);

        Assert.Equal(AlertLayerWorkArea.Unavailable, resolved);
    }

    [Fact]
    public void Unavailable_IsAllZero() =>
        Assert.Equal(new AlertLayerWorkArea(0, 0, 0, 0), AlertLayerWorkArea.Unavailable);
}
