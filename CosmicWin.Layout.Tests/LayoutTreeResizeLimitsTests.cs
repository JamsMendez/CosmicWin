using CosmicWin.Layout;

namespace CosmicWin.Layout.Tests;

/// <summary>
/// A resize that stops where the WINDOWS stop -- on both sides of the boundary it moves.
/// </summary>
/// <remarks>
/// <para>
/// Measured with NVIDIA Broadcast, which will not go under 772 tall and will not go over 1000.
/// Neither limit was known here, and every end of it was wrong in its own way.
/// </para>
/// <para>
/// SHRINKING past the floor became a tug of war: the chord took the slot to 703, the adapter gave
/// the space straight back to reach 772, and five presses produced five rounds with the window
/// ending where it began.
/// </para>
/// <para>
/// GROWING past the ceiling was worse, because nothing corrected it: the slot went to 1117, the
/// window stayed at 1000, and the neighbour was squashed to 258 in exchange for dead space.
/// </para>
/// <para>
/// And bounding only the node that MOVES moved the problem rather than solving it. Reported from
/// real use: growing a NEIGHBOUR upward pushed Broadcast's slot to 703 and then 634 while the chord
/// stayed inside its own limits the whole time. A window is invaded by its neighbour's resize
/// exactly as easily as by its own, so the node giving space up is asked as well.
/// </para>
/// <para>
/// The callback answers in plain lengths for any node. Nothing here knows what a window is and it
/// should not learn -- the caller measures, and the tree just stops.
/// </para>
/// </remarks>
public class LayoutTreeResizeLimitsTests
{
    private static GroupNode Pair(SplitAxis axis, int length, int first, int second)
    {
        var group = new GroupNode(axis) { GroupLength = length };
        var a = new LeafNode(new WindowRef(1));
        var b = new LeafNode(new WindowRef(2));
        LayoutTree.AddChild(group, a, index: 0);
        LayoutTree.AddChild(group, b, index: 1);
        group.Sizes[0] = first;
        group.Sizes[1] = second;
        return group;
    }

    /// <summary>Limits for one node, and none for anything else.</summary>
    private static Func<Node, SplitAxis, (int Min, int Max)> Only(Node bounded, int min, int max) =>
        (node, _) => ReferenceEquals(node, bounded) ? (min, max) : (0, int.MaxValue);

    /// <summary>Shrinking stops at the floor rather than going under it and being put back.</summary>
    [Fact]
    public void Resize_ShrinkingTowardTheFloor_StopsOnIt()
    {
        var group = Pair(SplitAxis.Vertical, 1376, 604, 772);

        // Down with no neighbour below shrinks the focused node; the default step would take 69.
        Assert.True(LayoutTree.ResizeNode(
            Direction.Down, group.Children[1], limitsOf: Only(group.Children[1], 740, int.MaxValue)));

        Assert.Equal(740, group.Sizes[1]);
        Assert.Equal(1376, group.Sizes.Sum());
    }

    /// <summary>And a node already on its floor does not move at all.</summary>
    [Fact]
    public void Resize_ShrinkingANodeAlreadyOnItsFloor_ChangesNothing()
    {
        var group = Pair(SplitAxis.Vertical, 1376, 604, 772);

        Assert.False(LayoutTree.ResizeNode(
            Direction.Down, group.Children[1], limitsOf: Only(group.Children[1], 772, int.MaxValue)));

        Assert.Equal([604, 772], group.Sizes);
    }

    /// <summary>Growing stops at the ceiling rather than buying dead space with a neighbour.</summary>
    [Fact]
    public void Resize_GrowingTowardTheCeiling_StopsOnIt()
    {
        var group = Pair(SplitAxis.Vertical, 1376, 604, 772);

        Assert.True(LayoutTree.ResizeNode(
            Direction.Up, group.Children[1], limitsOf: Only(group.Children[1], 0, 800)));

        Assert.Equal(800, group.Sizes[1]);
        Assert.Equal(576, group.Sizes[0]);
    }

    /// <summary>And a node already at its ceiling does not take the neighbour's space for nothing.</summary>
    [Fact]
    public void Resize_GrowingANodeAlreadyAtItsCeiling_ChangesNothing()
    {
        var group = Pair(SplitAxis.Vertical, 1376, 604, 772);

        Assert.False(LayoutTree.ResizeNode(
            Direction.Up, group.Children[1], limitsOf: Only(group.Children[1], 0, 772)));

        Assert.Equal([604, 772], group.Sizes);
    }

    /// <summary>
    /// The DONOR's floor stops the resize too: a neighbour may not be invaded down past what it
    /// will not go under.
    /// </summary>
    /// <remarks>
    /// The half that was missing, and the one the report was about. The node doing the growing is
    /// inside its own limits the entire time -- it has none -- and the damage lands entirely on the
    /// window beside it.
    /// </remarks>
    [Fact]
    public void Resize_GrowingIntoANeighbourWithAFloor_StopsOnTheNeighboursFloor()
    {
        var group = Pair(SplitAxis.Vertical, 1376, 604, 772);

        // The BOTTOM child grows upward into the top one, which will not go under 560.
        Assert.True(LayoutTree.ResizeNode(
            Direction.Up, group.Children[1], limitsOf: Only(group.Children[0], 560, int.MaxValue)));

        Assert.Equal(560, group.Sizes[0]);
        Assert.Equal(816, group.Sizes[1]);
    }

    /// <summary>And a neighbour already on its floor gives nothing at all.</summary>
    [Fact]
    public void Resize_GrowingIntoANeighbourAlreadyOnItsFloor_ChangesNothing()
    {
        var group = Pair(SplitAxis.Vertical, 1376, 604, 772);

        Assert.False(LayoutTree.ResizeNode(
            Direction.Up, group.Children[1], limitsOf: Only(group.Children[0], 604, int.MaxValue)));

        Assert.Equal([604, 772], group.Sizes);
    }

    /// <summary>
    /// Unset limits leave the resize exactly as it was, which is what every caller that has never
    /// measured a window relies on.
    /// </summary>
    [Fact]
    public void Resize_WithNoLimits_BehavesExactlyAsBefore()
    {
        var group = Pair(SplitAxis.Vertical, 1000, 500, 500);

        Assert.True(LayoutTree.ResizeNode(Direction.Down, group.Children[1]));

        Assert.Equal([550, 450], group.Sizes);
    }

    /// <summary>
    /// A branch is asked about as readily as a window, because the caller answers for whatever is
    /// inside it.
    /// </summary>
    /// <remarks>
    /// A resize walks up to the nearest matching-axis ancestor, and what moves there is often a
    /// whole branch -- a window with a floor is usually NESTED rather than a direct sibling of
    /// whoever is being resized. Nothing here decides what a branch's floor is; it asks, and the
    /// caller sums or maxes as the axis demands.
    /// </remarks>
    [Fact]
    public void Resize_MovingAWholeBranch_AsksAboutTheBranch()
    {
        var root = new GroupNode(SplitAxis.Horizontal) { GroupLength = 1000 };
        var nested = new GroupNode(SplitAxis.Vertical) { GroupLength = 600 };
        var deep = new LeafNode(new WindowRef(1));
        var alsoDeep = new LeafNode(new WindowRef(2));
        LayoutTree.AddChild(nested, deep, index: 0);
        LayoutTree.AddChild(nested, alsoDeep, index: 1);
        var other = new LeafNode(new WindowRef(3));
        LayoutTree.AddChild(root, nested, index: 0);
        LayoutTree.AddChild(root, other, index: 1);
        root.Sizes[0] = 600;
        root.Sizes[1] = 400;

        // `other` grows leftward into the branch, which will not go under 580.
        Assert.True(LayoutTree.ResizeNode(
            Direction.Left, other, limitsOf: Only(nested, 580, int.MaxValue)));

        Assert.Equal(580, root.Sizes[0]);
        Assert.Equal(420, root.Sizes[1]);
    }
}
