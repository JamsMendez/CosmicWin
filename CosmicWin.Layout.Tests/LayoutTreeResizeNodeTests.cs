using CosmicWin.Layout;

namespace CosmicWin.Layout.Tests;

public class LayoutTreeResizeNodeTests
{
    [Theory]
    [InlineData(Direction.Right, SplitAxis.Horizontal)]
    [InlineData(Direction.Down, SplitAxis.Vertical)]
    public void ResizeNode_DefaultStep_GrowsTargetAndShrinksDirectionalNeighbor(
        Direction direction,
        SplitAxis axis)
    {
        var group = Group(axis, 1000, 500, 500);

        var resized = LayoutTree.ResizeNode(direction, group.Children[0]);

        Assert.True(resized);
        Assert.Equal([550, 450], group.Sizes);
        Assert.Equal(group.GroupLength, group.Sizes.Sum());
    }

    [Theory]
    [InlineData(Direction.Left, SplitAxis.Horizontal)]
    [InlineData(Direction.Up, SplitAxis.Vertical)]
    public void ResizeNode_NegativeDirection_GrowsTargetFromOppositeNeighbor(
        Direction direction,
        SplitAxis axis)
    {
        var group = Group(axis, 1000, 450, 550);

        Assert.True(LayoutTree.ResizeNode(direction, group.Children[1]));

        Assert.Equal([400, 600], group.Sizes);
    }

    [Fact]
    public void ResizeNode_OrientationMismatch_WalksToMatchingGrandparent()
    {
        var root = Group(SplitAxis.Horizontal, 1000, 600, 400);
        var nested = Group(SplitAxis.Vertical, 600, 300, 300);
        nested.Parent = root;
        root.Children[0] = nested;

        Assert.True(LayoutTree.ResizeNode(Direction.Right, nested.Children[0]));

        Assert.Equal([650, 350], root.Sizes);
        Assert.Equal([300, 300], nested.Sizes);
    }

    [Fact]
    public void ResizeNode_NeighborNearMinimum_TransfersOnlyAvailableHeadroom()
    {
        var group = Group(SplitAxis.Horizontal, 1000, 880, 120);

        Assert.True(LayoutTree.ResizeNode(Direction.Right, group.Children[0]));

        Assert.Equal([900, 100], group.Sizes);
    }

    [Fact]
    public void ResizeNode_NeighborAtMinimum_IsNoOp()
    {
        var group = Group(SplitAxis.Horizontal, 1000, 900, 100);

        Assert.False(LayoutTree.ResizeNode(Direction.Right, group.Children[0]));

        Assert.Equal([900, 100], group.Sizes);
    }

    [Fact]
    public void ResizeNode_UsesRoundedStepAndCeilingMinimumForIntegerSizes()
    {
        var group = Group(SplitAxis.Horizontal, 333, 298, 35);

        Assert.True(LayoutTree.ResizeNode(Direction.Right, group.Children[0]));

        Assert.Equal([299, 34], group.Sizes);
        Assert.True(group.Sizes[1] / (double)group.GroupLength >= LayoutTree.DefaultMinRatio);
        Assert.Equal(333, group.Sizes.Sum());
    }

    [Fact]
    public void ResizeNode_CustomStep_UsesRequestedFractionOfAncestorLength()
    {
        var group = Group(SplitAxis.Horizontal, 800, 400, 400);

        Assert.True(LayoutTree.ResizeNode(Direction.Right, group.Children[0], step: 0.025));

        Assert.Equal([420, 380], group.Sizes);
    }

    [Fact]
    public void ResizeNode_NoMatchingBoundary_IsNoOp()
    {
        var group = Group(SplitAxis.Vertical, 1000, 500, 500);

        Assert.False(LayoutTree.ResizeNode(Direction.Right, group.Children[0]));
        Assert.Equal([500, 500], group.Sizes);
    }

    /// <summary>
    /// REPLACES the MR-3 pin . That fact asserted this exact scenario -- Ctrl+Alt+H on
    /// the leftmost of two tiled windows -- was "correct, spec-compliant behavior, not a defect",
    /// on the grounds that LE-6 step 2 documents a no-op at a group boundary. The investigation was
    /// right about the code and wrong about the spec: LE-6 is an incomplete port of the reference implementation,
    /// which carries a shrink as a first-class intent, so the boundary no-op was never the whole
    /// story. The maintainer re-reported it from real use twice before it was believed.
    /// <para>
    /// What survives is the part that IS a genuine boundary: a group whose axis does not match the
    /// requested direction anywhere up the tree still has nothing to resize either way.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(Direction.Left, SplitAxis.Vertical)]
    [InlineData(Direction.Up, SplitAxis.Horizontal)]
    public void ResizeNode_NoMatchingAxisAnywhereUpTheTree_IsNoOpInEitherIntent(
        Direction direction,
        SplitAxis axis)
    {
        var group = Group(axis, 1000, 500, 500);

        Assert.False(LayoutTree.ResizeNode(direction, group.Children[0]));
        Assert.Equal([500, 500], group.Sizes);
    }

    /// <summary>
    /// Reported from real use: "el resize decremental no funciona". It never did
    /// there was no shrink in the engine AT ALL. LE-6 only ever grew the focused subtree by taking
    /// from a neighbour on the pressed side, so the leading child of a group, having no neighbour
    /// that way, could only ever get bigger.
    /// <para>
    /// the reference implementation separates the two intents: <c>ResizeDirection::{Inwards,Outwards}</c> chooses
    /// shrink or grow, <c>ResizeEdge</c> chooses which boundary moves, and Inwards flips the edge
    /// (<c>input/mod.rs:2271</c>). It reaches that through a resize MODE with an on-screen
    /// indicator, which CosmicWin has no equivalent of. With four chords and no mode, the direction
    /// names which way the BOUNDARY travels: grow into the neighbour on the pressed side when there
    /// is one, otherwise push the opposite boundary the same way, which shrinks. Both operations
    /// stay reachable and no existing grow changes meaning.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(Direction.Left, SplitAxis.Horizontal)]
    [InlineData(Direction.Up, SplitAxis.Vertical)]
    public void ResizeNode_LeadingChildPushedOutward_ShrinksItselfInsteadOfDoingNothing(
        Direction direction,
        SplitAxis axis)
    {
        var group = Group(axis, 1000, 500, 500);

        Assert.True(LayoutTree.ResizeNode(direction, group.Children[0]));
        Assert.Equal([450, 550], group.Sizes);
        Assert.Equal(group.GroupLength, group.Sizes.Sum());
    }

    /// <summary>The mirror: the TRAILING child has no neighbour to the Right/Down, so it shrinks too.</summary>
    [Theory]
    [InlineData(Direction.Right, SplitAxis.Horizontal)]
    [InlineData(Direction.Down, SplitAxis.Vertical)]
    public void ResizeNode_TrailingChildPushedOutward_ShrinksItself(
        Direction direction,
        SplitAxis axis)
    {
        var group = Group(axis, 1000, 500, 500);

        Assert.True(LayoutTree.ResizeNode(direction, group.Children[1]));
        Assert.Equal([550, 450], group.Sizes);
        Assert.Equal(group.GroupLength, group.Sizes.Sum());
    }

    /// <summary>
    /// A shrink stops at the same floor a grow does, applied to the node giving space up rather
    /// than the one receiving it -- otherwise repeated presses would squeeze a window to nothing.
    /// </summary>
    [Fact]
    public void ResizeNode_ShrinkingFromMinimum_IsNoOp()
    {
        var group = Group(SplitAxis.Horizontal, 1000, 100, 900);

        Assert.False(LayoutTree.ResizeNode(Direction.Left, group.Children[0]));
        Assert.Equal([100, 900], group.Sizes);
    }

    private static GroupNode Group(SplitAxis axis, int length, params int[] sizes)
    {
        var group = new GroupNode(axis) { GroupLength = length };
        for (int index = 0; index < sizes.Length; index++)
        {
            var child = new LeafNode(new WindowRef(index + 1)) { Parent = group };
            group.Children.Add(child);
            group.Sizes.Add(sizes[index]);
        }

        return group;
    }
}
