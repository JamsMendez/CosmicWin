using CosmicWin.Interop;
using CosmicWin.Layout;

namespace CosmicWin.App.Tests;

/// <summary>
/// What Ctrl+Alt+&lt;direction&gt; means for a window in no tree -- with tiling switched off, every
/// window there is.
/// </summary>
/// <remarks>
/// The rule under test is <c>LayoutTree.ResizeNode</c>'s, read against the work area: grow into the
/// pressed side while there is room, and once there is none push the opposite edge in instead. The
/// facts below pin the mirroring, not a new invention -- the whole value of the behaviour is that
/// the chord feels the same with the switch either way.
/// </remarks>
public sealed class FloatingResizeTests
{
    private static readonly Rectangle WorkArea = Rectangle.FromSize(0, 0, 1920, 1080);

    /// <summary>5% of 1920, the step the tiled path would take from a group of that length.</summary>
    private const int SidewaysStep = 96;

    /// <summary>5% of 1080.</summary>
    private const int UprightStep = 54;

    [Fact]
    public void PressingRight_WithRoomOnThatSide_GrowsRightwardAndPinsTheLeftEdge()
    {
        var window = Rectangle.FromSize(200, 100, 800, 600);

        var resized = FloatingResize.Apply(window, WorkArea, Direction.Right);

        Assert.Equal(Rectangle.FromSize(200, 100, 800 + SidewaysStep, 600), resized);
    }

    /// <summary>
    /// The half of the rule that makes a decremental resize possible at all. In the tree it is
    /// reached by a leaf with no sibling on the pressed side; out here, by a window already against
    /// the work area.
    /// </summary>
    [Fact]
    public void PressingRight_AgainstTheWorkArea_ShrinksFromTheLeftAndPinsTheRightEdge()
    {
        // Right edge exactly on the work area's: there is nothing that way.
        var window = Rectangle.FromSize(1120, 100, 800, 600);

        var resized = FloatingResize.Apply(window, WorkArea, Direction.Right);

        Assert.Equal(Rectangle.FromSize(1120 + SidewaysStep, 100, 800 - SidewaysStep, 600), resized);
        Assert.Equal(WorkArea.Right, resized!.Value.Right);
    }

    [Fact]
    public void PressingLeft_WithRoomOnThatSide_GrowsLeftwardAndPinsTheRightEdge()
    {
        var window = Rectangle.FromSize(200, 100, 800, 600);

        var resized = FloatingResize.Apply(window, WorkArea, Direction.Left);

        Assert.Equal(Rectangle.FromSize(200 - SidewaysStep, 100, 800 + SidewaysStep, 600), resized);
        Assert.Equal(1000, resized!.Value.Right);
    }

    [Fact]
    public void PressingLeft_AgainstTheWorkArea_ShrinksFromTheRightAndPinsTheLeftEdge()
    {
        var window = Rectangle.FromSize(0, 100, 800, 600);

        var resized = FloatingResize.Apply(window, WorkArea, Direction.Left);

        Assert.Equal(Rectangle.FromSize(0, 100, 800 - SidewaysStep, 600), resized);
    }

    [Fact]
    public void PressingDown_WithRoomBelow_GrowsDownwardAndPinsTheTopEdge()
    {
        var window = Rectangle.FromSize(200, 100, 800, 600);

        var resized = FloatingResize.Apply(window, WorkArea, Direction.Down);

        Assert.Equal(Rectangle.FromSize(200, 100, 800, 600 + UprightStep), resized);
    }

    [Fact]
    public void PressingUp_AgainstTheWorkArea_ShrinksFromTheBottomAndPinsTheTopEdge()
    {
        var window = Rectangle.FromSize(200, 0, 800, 600);

        var resized = FloatingResize.Apply(window, WorkArea, Direction.Up);

        Assert.Equal(Rectangle.FromSize(200, 0, 800, 600 - UprightStep), resized);
    }

    /// <summary>
    /// The step is a fraction of the WORK AREA, not of the window -- the work area standing in for
    /// the ancestor group whose length the tiled step is taken from. Measured from the window
    /// instead, a small window would creep and a large one would leap.
    /// </summary>
    [Fact]
    public void TheStepIsAFractionOfTheWorkArea_NotOfTheWindow()
    {
        var small = FloatingResize.Apply(Rectangle.FromSize(200, 100, 100, 100), WorkArea, Direction.Right);
        var large = FloatingResize.Apply(Rectangle.FromSize(200, 100, 1000, 100), WorkArea, Direction.Right);

        Assert.Equal(100 + SidewaysStep, small!.Value.Width);
        Assert.Equal(1000 + SidewaysStep, large!.Value.Width);
    }

    /// <summary>A grow stops at the work area rather than hanging the window off the edge.</summary>
    [Fact]
    public void AGrowIsClampedToTheWorkArea_WhenLessThanAFullStepIsLeft()
    {
        // 40px of room on the right, and a step of 96 would ask for more than exists.
        var window = Rectangle.FromSize(1080, 100, 800, 600);

        var resized = FloatingResize.Apply(window, WorkArea, Direction.Right);

        Assert.Equal(Rectangle.FromSize(1080, 100, 840, 600), resized);
        Assert.Equal(WorkArea.Right, resized!.Value.Right);
    }

    /// <summary>
    /// A size the window has already demonstrated it will not go over. Measured on NVIDIA
    /// Broadcast, which will not go over 1000 tall -- the tiled path reads the same ceiling, and a
    /// chord out here that ignored it would produce a rectangle nobody ever occupies.
    /// </summary>
    [Fact]
    public void AGrowStopsAtTheWindowsOwnCeiling()
    {
        var window = Rectangle.FromSize(200, 100, 800, 600);

        var resized = FloatingResize.Apply(
            window, WorkArea, Direction.Right,
            limits: (MinWidth: 0, MinHeight: 0, MaxWidth: 850, MaxHeight: int.MaxValue));

        Assert.Equal(850, resized!.Value.Width);
    }

    /// <summary>And the floor, which is the half that used to become a tug of war.</summary>
    [Fact]
    public void AShrinkStopsAtTheWindowsOwnFloor()
    {
        var window = Rectangle.FromSize(1120, 100, 800, 600);

        var resized = FloatingResize.Apply(
            window, WorkArea, Direction.Right,
            limits: (MinWidth: 760, MinHeight: 0, MaxWidth: int.MaxValue, MaxHeight: int.MaxValue));

        Assert.Equal(760, resized!.Value.Width);
        Assert.Equal(WorkArea.Right, resized.Value.Right);
    }

    /// <summary>
    /// A window with nowhere left to go is REFUSED rather than handed back its own rectangle, so
    /// the caller can leave it alone instead of writing a position that changes nothing.
    /// </summary>
    [Fact]
    public void AWindowAlreadyAtItsFloorIsRefused()
    {
        var window = Rectangle.FromSize(1120, 100, 800, 600);

        var resized = FloatingResize.Apply(
            window, WorkArea, Direction.Right,
            limits: (MinWidth: 800, MinHeight: 0, MaxWidth: int.MaxValue, MaxHeight: int.MaxValue));

        Assert.Null(resized);
    }

    /// <summary>
    /// Refused rather than quietly turned into a shrink. The user asked for room on a side that has
    /// none AND cannot give any, and taking room away instead would be answering a different chord.
    /// </summary>
    [Fact]
    public void AWindowAlreadyAtItsCeilingIsRefused()
    {
        var window = Rectangle.FromSize(200, 100, 800, 600);

        var resized = FloatingResize.Apply(
            window, WorkArea, Direction.Right,
            limits: (MinWidth: 0, MinHeight: 0, MaxWidth: 800, MaxHeight: int.MaxValue));

        Assert.Null(resized);
    }

    /// <summary>
    /// A window hanging OUTSIDE the work area -- which one dragged off the edge genuinely is -- has
    /// negative room on that side. Reading that as room would hand the grow a transfer of the wrong
    /// sign and jump the window instead of nudging it.
    /// </summary>
    [Fact]
    public void AWindowHangingPastTheWorkAreaShrinksRatherThanJumping()
    {
        var window = Rectangle.FromSize(1500, 100, 800, 600);

        var resized = FloatingResize.Apply(window, WorkArea, Direction.Right);

        Assert.Equal(Rectangle.FromSize(1500 + SidewaysStep, 100, 800 - SidewaysStep, 600), resized);
    }

    /// <summary>A window with no area is not a rectangle to resize, whatever the chord says.</summary>
    [Fact]
    public void AWindowWithNoAreaIsRefused()
    {
        Assert.Null(FloatingResize.Apply(Rectangle.FromSize(0, 0, 0, 0), WorkArea, Direction.Right));
    }
}
