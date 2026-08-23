using CosmicWin.Interop;
using CosmicWin.Layout;

namespace CosmicWin.App.Tests;

/// <summary>
/// Where a modal dialog is put when it opens: centred on the work area, at the size it asked for.
/// </summary>
/// <remarks>
/// Size is never touched. A dialog lays itself out for its own content -- a message, two buttons --
/// and resizing one is how text gets clipped and buttons end up off the edge. The only decision
/// here is the origin.
/// </remarks>
public sealed class DialogPlacementTests
{
    private static readonly Rectangle WorkArea = Rectangle.FromSize(0, 0, 1920, 1080);

    /// <summary>The size the dialog opened at, which is what a snap-down restores it to.</summary>
    private static readonly Rectangle Restore = Rectangle.FromSize(0, 0, 400, 200);

    [Fact]
    public void ADialogIsCentredOnTheWorkArea_AtTheSizeItAskedFor()
    {
        var centred = DialogPlacement.Centre(Rectangle.FromSize(0, 0, 400, 200), WorkArea);

        Assert.Equal(Rectangle.FromSize(760, 440, 400, 200), centred);
    }

    /// <summary>A monitor that does not start at the origin -- a second display, or a taskbar on the left.</summary>
    [Fact]
    public void TheWorkAreasOwnOriginIsRespected()
    {
        var centred = DialogPlacement.Centre(
            Rectangle.FromSize(0, 0, 400, 200), Rectangle.FromSize(1920, 100, 1280, 720));

        Assert.Equal(Rectangle.FromSize(2360, 360, 400, 200), centred);
    }

    /// <summary>
    /// A dialog wider or taller than the work area must land ON the work area, not centred around
    /// it -- centring a 2000px dialog on a 1920px screen puts its left edge at -40, taking the close
    /// button and the title bar off screen with it. Clamped, the user can still reach both.
    /// </summary>
    [Fact]
    public void ADialogLargerThanTheWorkArea_IsClampedToItsOrigin_NotCentredOffScreen()
    {
        var centred = DialogPlacement.Centre(Rectangle.FromSize(0, 0, 2400, 1400), WorkArea);

        Assert.Equal(Rectangle.FromSize(0, 0, 2400, 1400), centred);
    }

    /// <summary>Its current position is irrelevant -- only its size and the work area decide.</summary>
    [Fact]
    public void WhereTheDialogOpenedIsIgnored()
    {
        var fromTheCorner = DialogPlacement.Centre(Rectangle.FromSize(1500, 900, 400, 200), WorkArea);
        var fromTheOrigin = DialogPlacement.Centre(Rectangle.FromSize(0, 0, 400, 200), WorkArea);

        Assert.Equal(fromTheOrigin, fromTheCorner);
    }

    /// <summary>An odd remainder rounds one way, deterministically, rather than drifting by a pixel per call.</summary>
    [Fact]
    public void AnOddDifferenceIsRoundedTheSameWayEveryTime()
    {
        var once = DialogPlacement.Centre(Rectangle.FromSize(0, 0, 401, 201), WorkArea);
        var twice = DialogPlacement.Centre(once, WorkArea);

        Assert.Equal(once, twice);
    }

    /// <summary>
    /// A floating dialog answers the move chord by SNAPPING, since it has no tile to travel between.
    /// Left and right take a half, up takes the whole work area, and down returns it to the size it
    /// opened at, centred.
    /// </summary>
    [Fact]
    public void SnappingLeftTakesTheLeftHalf()
    {
        var snapped = DialogPlacement.Snap(
            Rectangle.FromSize(760, 440, 400, 200), WorkArea, Direction.Left, Restore);

        Assert.Equal(Rectangle.FromSize(0, 0, 960, 1080), snapped);
    }

    [Fact]
    public void SnappingRightTakesTheRightHalf()
    {
        var snapped = DialogPlacement.Snap(
            Rectangle.FromSize(760, 440, 400, 200), WorkArea, Direction.Right, Restore);

        Assert.Equal(Rectangle.FromSize(960, 0, 960, 1080), snapped);
    }

    [Fact]
    public void SnappingUpTakesTheWholeWorkArea()
    {
        var snapped = DialogPlacement.Snap(
            Rectangle.FromSize(760, 440, 400, 200), WorkArea, Direction.Up, Restore);

        Assert.Equal(WorkArea, snapped);
    }

    /// <summary>
    /// Down restores the size the dialog OPENED at, not the size it currently wears. After a snap it
    /// is half the screen wide, and re-centring that would be a different window from the one the
    /// application laid out for its own content.
    /// </summary>
    [Fact]
    public void SnappingDownReturnsItToTheSizeItOpenedAt_Centred()
    {
        var halfScreen = Rectangle.FromSize(0, 0, 960, 1080);

        var snapped = DialogPlacement.Snap(halfScreen, WorkArea, Direction.Down, Restore);

        Assert.Equal(Rectangle.FromSize(760, 440, 400, 200), snapped);
    }

    /// <summary>
    /// The two halves must tile the work area EXACTLY, leaving no seam and no overlap on an odd
    /// width. The right half takes the remainder, which is the only way 1921 splits without a gap.
    /// </summary>
    [Fact]
    public void OnAnOddWidth_TheTwoHalvesMeetWithNoGapAndNoOverlap()
    {
        var odd = Rectangle.FromSize(0, 0, 1921, 1080);
        var dialog = Rectangle.FromSize(0, 0, 400, 200);

        var left = DialogPlacement.Snap(dialog, odd, Direction.Left, Restore);
        var right = DialogPlacement.Snap(dialog, odd, Direction.Right, Restore);

        Assert.Equal(left.Right, right.Left);
        Assert.Equal(odd.Left, left.Left);
        Assert.Equal(odd.Right, right.Right);
    }

    /// <summary>A work area that does not start at the origin -- a second display, or a taskbar on the left.</summary>
    [Fact]
    public void SnappingRespectsTheWorkAreasOwnOrigin()
    {
        var secondary = Rectangle.FromSize(1920, 100, 1280, 720);

        var snapped = DialogPlacement.Snap(
            Rectangle.FromSize(0, 0, 400, 200), secondary, Direction.Right, Restore);

        Assert.Equal(Rectangle.FromSize(2560, 100, 640, 720), snapped);
    }
}
