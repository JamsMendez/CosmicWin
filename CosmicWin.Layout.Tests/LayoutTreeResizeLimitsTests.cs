using CosmicWin.Layout;

namespace CosmicWin.Layout.Tests;

/// <summary>
/// A resize that stops where the window itself stops.
/// </summary>
/// <remarks>
/// <para>
/// Measured with NVIDIA Broadcast, which will not go under 772 tall and will not go over 1000.
/// Neither limit was known here, and both ends were wrong in their own way.
/// </para>
/// <para>
/// SHRINKING past the floor became a tug of war: the chord took the slot to 703, the adapter gave
/// the space straight back to reach 772, and five presses produced five shrink-and-restore rounds
/// with the window ending exactly where it began.
/// </para>
/// <para>
/// GROWING past the ceiling was worse, because nothing corrected it: the slot went to 1117, the
/// window stayed at 1000, and the neighbour was squashed to 258 in exchange for dead space.
/// </para>
/// <para>
/// The numbers are plain lengths. Nothing in this project knows what a window is, and it should not
/// learn -- the caller measures the limits and passes them in.
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

    /// <summary>Shrinking stops at the floor rather than going under it and being put back.</summary>
    [Fact]
    public void Resize_ShrinkingTowardTheFloor_StopsOnIt()
    {
        var group = Pair(SplitAxis.Vertical, 1376, 604, 772);

        // Down with no neighbour below shrinks the focused node; the default step would take 68.
        Assert.True(LayoutTree.ResizeNode(Direction.Down, group.Children[1], minLength: 740));

        Assert.Equal(740, group.Sizes[1]);
        Assert.Equal(1376, group.Sizes.Sum());
    }

    /// <summary>And a node already on its floor does not move at all.</summary>
    [Fact]
    public void Resize_ShrinkingANodeAlreadyOnItsFloor_ChangesNothing()
    {
        var group = Pair(SplitAxis.Vertical, 1376, 604, 772);

        Assert.False(LayoutTree.ResizeNode(Direction.Down, group.Children[1], minLength: 772));

        Assert.Equal([604, 772], group.Sizes);
    }

    /// <summary>Growing stops at the ceiling rather than buying dead space with a neighbour.</summary>
    [Fact]
    public void Resize_GrowingTowardTheCeiling_StopsOnIt()
    {
        var group = Pair(SplitAxis.Vertical, 1376, 604, 772);

        Assert.True(LayoutTree.ResizeNode(Direction.Up, group.Children[1], maxLength: 800));

        Assert.Equal(800, group.Sizes[1]);
        Assert.Equal(576, group.Sizes[0]);
    }

    /// <summary>And a node already at its ceiling does not take the neighbour's space for nothing.</summary>
    [Fact]
    public void Resize_GrowingANodeAlreadyAtItsCeiling_ChangesNothing()
    {
        var group = Pair(SplitAxis.Vertical, 1376, 604, 772);

        Assert.False(LayoutTree.ResizeNode(Direction.Up, group.Children[1], maxLength: 772));

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
    /// The limits belong to the WINDOW, so they are ignored when the thing being resized is a
    /// subtree rather than that window.
    /// </summary>
    /// <remarks>
    /// A resize walks up to the nearest ancestor whose axis matches, and what moves there may be a
    /// whole branch holding several windows. One window's floor says nothing about how short that
    /// branch may be, and applying it would let a single constrained window pin a group it merely
    /// happens to live in.
    /// </remarks>
    [Fact]
    public void Resize_ThatMovesAWholeBranch_IgnoresOneWindowsLimits()
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

        // The nested group is what moves in the root, not `deep` -- so `deep`'s ceiling is none of
        // the root's business.
        Assert.True(LayoutTree.ResizeNode(Direction.Right, deep, maxLength: 10));

        Assert.Equal(650, root.Sizes[0]);
    }
}
