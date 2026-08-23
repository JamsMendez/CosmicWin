using CosmicWin.Interop;

namespace CosmicWin.App.Tests;

/// <summary>
/// Where the focus border is drawn, and how thick it is allowed to get.
/// </summary>
/// <remarks>
/// <para>
/// The border is drawn OUTSIDE the window rather than over it, so it never covers content the
/// application put at its own edge. That is only possible because tiles are already separated: the
/// arranger insets every tile by <c>Gap / 2</c>, which leaves exactly <c>Gap</c> between neighbours
/// and <c>Gap / 2</c> against the work area.
/// </para>
/// <para>
/// Which is also the hard limit. Two neighbours each grow toward the other, so anything thicker
/// than <c>Gap / 2</c> makes their borders overlap -- and against the work-area edge it simply falls
/// off the screen. The rule is arithmetic, not taste, so it is pinned here rather than left as a
/// comment on a constant.
/// </para>
/// </remarks>
public sealed class BorderGeometryTests
{
    [Fact]
    public void TheBorderSitsOutsideTheWindow_OnAllFourSides()
    {
        var around = BorderGeometry.Around(Rectangle.FromSize(100, 100, 400, 300), thickness: 3);

        Assert.Equal(Rectangle.FromSize(97, 97, 406, 306), around);
    }

    /// <summary>Nothing to draw is not a special case: the rectangle is simply the window's own.</summary>
    [Fact]
    public void AThicknessOfZeroChangesNothing()
    {
        var window = Rectangle.FromSize(100, 100, 400, 300);

        Assert.Equal(window, BorderGeometry.Around(window, thickness: 0));
    }

    /// <summary>
    /// A negative thickness would shrink the rectangle and draw the border INSIDE the window, over
    /// content. Clamped rather than trusted, because the value reaches here from a constant that
    /// somebody will eventually edit.
    /// </summary>
    [Fact]
    public void ANegativeThicknessIsClampedToNothing()
    {
        var window = Rectangle.FromSize(100, 100, 400, 300);

        Assert.Equal(window, BorderGeometry.Around(window, thickness: -5));
    }

    [Theory]
    [InlineData(8, 4)]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(20, 10)]
    public void TheThickestBorderThatFitsIsHalfTheGap(int gap, int expected)
    {
        Assert.Equal(expected, BorderGeometry.MaxThicknessForGap(gap));
    }

    /// <summary>
    /// The reason that limit exists, stated as geometry rather than as a claim: two tiles separated
    /// by exactly one gap, each wearing the thickest border allowed, MEET and do not overlap.
    /// </summary>
    [Fact]
    public void AtTheMaximumThickness_NeighbouringBordersMeetWithoutOverlapping()
    {
        const int gap = 8;
        var thickness = BorderGeometry.MaxThicknessForGap(gap);

        var left = Rectangle.FromSize(0, 0, 100, 100);
        var right = Rectangle.FromSize(left.Right + gap, 0, 100, 100);

        var aroundLeft = BorderGeometry.Around(left, thickness);
        var aroundRight = BorderGeometry.Around(right, thickness);

        Assert.Equal(aroundLeft.Right, aroundRight.Left);
    }

    /// <summary>One pixel over the limit is the first overlap, which is what makes the limit exact.</summary>
    [Fact]
    public void OnePixelOverTheMaximum_NeighbouringBordersOverlap()
    {
        const int gap = 8;
        var tooThick = BorderGeometry.MaxThicknessForGap(gap) + 1;

        var left = Rectangle.FromSize(0, 0, 100, 100);
        var right = Rectangle.FromSize(left.Right + gap, 0, 100, 100);

        Assert.True(BorderGeometry.Around(left, tooThick).Right > BorderGeometry.Around(right, tooThick).Left);
    }

    /// <summary>
    /// The border sits OUTSIDE the window, so a corner concentric with the window's own is the
    /// window's radius plus the border's thickness. Using the window's radius unchanged would draw a
    /// tighter arc than the corner it follows, and the gap between the two would widen at 45 degrees.
    /// </summary>
    [Theory]
    [InlineData(8, 2, 10)]
    [InlineData(8, 0, 8)]
    [InlineData(0, 2, 2)]
    public void TheCornerRadiusGrowsWithTheBorder(int windowRadius, int thickness, int expected)
    {
        Assert.Equal(expected, BorderGeometry.CornerRadiusAround(windowRadius, thickness));
    }

    /// <summary>Negative input cannot produce a negative radius, which WPF rejects outright.</summary>
    [Theory]
    [InlineData(-8, 2)]
    [InlineData(8, -20)]
    public void TheCornerRadiusIsNeverNegative(int windowRadius, int thickness)
    {
        Assert.True(BorderGeometry.CornerRadiusAround(windowRadius, thickness) >= 0);
    }
}
