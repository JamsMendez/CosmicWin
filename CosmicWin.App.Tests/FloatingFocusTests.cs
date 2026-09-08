using CosmicWin.Interop;
using CosmicWin.Layout;

namespace CosmicWin.App.Tests;

/// <summary>
/// Pins <see cref="FloatingFocus.Toward"/>'s two-pass geometry in isolation, against plain
/// rectangles -- no tree, no registry, no Win32. The AHEAD pass must agree with
/// <c>ActionExecutor.NearestTileToward</c>'s centre-distance comparison, since a tile and a
/// floating window are asked the same question ("what is nearest in this direction") and must
/// answer it the same way; the STACK pass is the part a tiled tree never needed at all.
/// </summary>
public sealed class FloatingFocusTests
{
    private static readonly nint Origin = new(1);

    /// <summary>100x100 centred at (550, 550), the same origin every AHEAD-pass test measures from.</summary>
    private static readonly Rectangle From = Rectangle.FromSize(500, 500, 100, 100);

    private static (nint Handle, Rectangle Bounds) At(nint handle, int centerX, int centerY, int size = 100) =>
        (handle, Rectangle.FromSize(centerX - (size / 2), centerY - (size / 2), size, size));

    [Fact]
    public void Toward_Right_PicksTheNearestCandidateAheadOfIt()
    {
        var near = At(new IntPtr(2), 700, 550);
        var far = At(new IntPtr(3), 900, 550);

        var target = FloatingFocus.Toward(From, Origin, Direction.Right, [near, far]);

        Assert.Equal(near.Handle, target);
    }

    [Fact]
    public void Toward_Left_PicksTheNearestCandidateAheadOfIt()
    {
        var near = At(new IntPtr(2), 400, 550);
        var far = At(new IntPtr(3), 200, 550);

        var target = FloatingFocus.Toward(From, Origin, Direction.Left, [near, far]);

        Assert.Equal(near.Handle, target);
    }

    [Fact]
    public void Toward_Up_PicksTheNearestCandidateAheadOfIt()
    {
        var near = At(new IntPtr(2), 550, 400);
        var far = At(new IntPtr(3), 550, 200);

        var target = FloatingFocus.Toward(From, Origin, Direction.Up, [near, far]);

        Assert.Equal(near.Handle, target);
    }

    [Fact]
    public void Toward_Down_PicksTheNearestCandidateAheadOfIt()
    {
        var near = At(new IntPtr(2), 550, 700);
        var far = At(new IntPtr(3), 550, 900);

        var target = FloatingFocus.Toward(From, Origin, Direction.Down, [near, far]);

        Assert.Equal(near.Handle, target);
    }

    /// <summary>
    /// The origin's own entry sits exactly where AHEAD would otherwise pick it -- proving the skip
    /// is about the HANDLE, not about a window that merely fails to move focus anywhere new.
    /// </summary>
    [Fact]
    public void Toward_NeverChoosesTheOriginsOwnHandle()
    {
        var self = (Origin, Rectangle.FromSize(650, 500, 100, 100));
        var real = At(new IntPtr(2), 900, 550);

        var target = FloatingFocus.Toward(From, Origin, Direction.Right, [self, real]);

        Assert.Equal(real.Handle, target);
    }

    /// <summary>
    /// A zero-area candidate is skipped exactly where <c>WindowFilters.IsAutoExcluded</c>'s own
    /// zero-area clause is: it is not a window on screen, whatever its centre coordinates claim.
    /// </summary>
    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    public void Toward_SkipsAZeroSizeCandidate(int width, int height)
    {
        var zeroSize = (new IntPtr(2), Rectangle.FromSize(650, 500, width, height));
        var real = At(new IntPtr(3), 900, 550);

        var target = FloatingFocus.Toward(From, Origin, Direction.Right, [zeroSize, real]);

        Assert.Equal(real.Handle, target);
    }

    /// <summary>
    /// Same primary-axis distance, different perpendicular distance: the smaller perpendicular
    /// distance wins, exactly as <c>NearestTileToward</c>'s own remarks describe for tiles.
    /// </summary>
    [Fact]
    public void Toward_TiesOnPrimaryDistance_BreakOnTheSmallerPerpendicularDistance()
    {
        var farOffAxis = At(new IntPtr(2), 700, 600);
        var closeToAxis = At(new IntPtr(3), 700, 520);

        var target = FloatingFocus.Toward(From, Origin, Direction.Right, [farOffAxis, closeToAxis]);

        Assert.Equal(closeToAxis.Handle, target);
    }

    /// <summary>
    /// Identical on BOTH distances: the one earlier in the list wins, because nothing else is left
    /// to decide between them.
    /// </summary>
    [Fact]
    public void Toward_TiesOnBothDistances_BreakOnTheEarlierListPosition()
    {
        var first = At(new IntPtr(2), 700, 550);
        var second = At(new IntPtr(3), 700, 550);

        var target = FloatingFocus.Toward(From, Origin, Direction.Right, [first, second]);

        Assert.Equal(first.Handle, target);
    }

    /// <summary>
    /// Nothing lies in the pressed direction and nothing overlaps the origin either: null is the
    /// honest answer, not a fallback to whatever else happens to be on screen -- the same rule
    /// <c>NearestTileToward</c> already obeys for a tile at the edge of the tree.
    /// </summary>
    [Fact]
    public void Toward_WithNothingAheadAndNothingOverlapping_ReturnsNull()
    {
        var behind = At(new IntPtr(2), 200, 550);

        var target = FloatingFocus.Toward(From, Origin, Direction.Right, [behind]);

        Assert.Null(target);
    }

    /// <summary>
    /// Nothing lies to the right, but two windows sit piled directly on the origin: the STACK pass
    /// walks to the next one BEHIND the origin in z-order, not the topmost one in the list.
    /// </summary>
    [Fact]
    public void Toward_WithNothingAhead_WalksToTheNextOverlappingWindowBehindTheOrigin()
    {
        var origin = (Origin, From);
        var behind1 = (new IntPtr(2), From);
        var behind2 = (new IntPtr(3), From);

        var target = FloatingFocus.Toward(From, Origin, Direction.Right, [origin, behind1, behind2]);

        Assert.Equal(behind1.Item1, target);
    }

    /// <summary>
    /// The whole point of walking by Z-ORDER rather than snapping to one target: a second press from
    /// the window the first press landed on continues DEEPER into the pile.
    /// </summary>
    [Fact]
    public void Toward_CalledAgainFromTheWindowItLandedOn_ContinuesDeeperIntoTheStack()
    {
        var origin = (Origin, From);
        var behind1 = (new IntPtr(2), From);
        var behind2 = (new IntPtr(3), From);
        var candidates = new[] { origin, behind1, behind2 };

        var first = FloatingFocus.Toward(From, Origin, Direction.Right, candidates);
        var second = FloatingFocus.Toward(From, first!.Value, Direction.Right, candidates);

        Assert.Equal(behind1.Item1, first);
        Assert.Equal(behind2.Item1, second);
    }

    /// <summary>
    /// A window behind the origin in z-order that does NOT overlap it is not a stack member -- it is
    /// simply somewhere else on screen with nothing ahead of it either, and must not be picked.
    /// </summary>
    [Fact]
    public void Toward_ANonOverlappingWindowBehindTheOriginInZOrder_IsNotChosen()
    {
        var origin = (Origin, From);
        // Deliberately NOT ahead of the origin in the direction pressed either (its centre sits to
        // the LEFT of the origin's), so the AHEAD pass cannot answer this test by accident -- only
        // the STACK pass's own overlap check can be what skips it.
        var elsewhere = At(new IntPtr(2), 200, 550);
        var overlapping = (new IntPtr(3), From);

        var target = FloatingFocus.Toward(From, Origin, Direction.Right, [origin, elsewhere, overlapping]);

        Assert.Equal(overlapping.Item1, target);
    }

    /// <summary>
    /// When the origin's own handle is not in the candidate list at all -- it did not survive
    /// whatever filter built it -- the stack pass has no position to search after, so it takes the
    /// first overlapping candidate rather than finding none.
    /// </summary>
    [Fact]
    public void Toward_WhenOriginHandleIsNotInTheList_StackPassTakesTheFirstOverlappingCandidate()
    {
        var overlapping = (new IntPtr(2), From);

        var target = FloatingFocus.Toward(From, Origin, Direction.Right, [overlapping]);

        Assert.Equal(overlapping.Item1, target);
    }
}
