using CosmicWin.Layout;

namespace CosmicWin.Layout.Tests;

/// <summary>
/// Mouse-driven resize: the boundary the user actually dragged, translated into the tree so the
/// reflow that follows keeps the new proportions instead of undoing them.
/// </summary>
public class LayoutTreeApplyEdgeDragTests
{
    [Fact]
    public void ApplyEdgeDrag_TrailingEdgePulledOut_TakesExactlyTheDraggedPixelsFromTheNeighbor()
    {
        var group = Group(SplitAxis.Horizontal, 1000, 500, 500);

        var applied = LayoutTree.ApplyEdgeDrag(
            group.Children[0],
            new Rect(0, 0, 500, 800),
            new Rect(0, 0, 580, 800));

        Assert.True(applied);
        Assert.Equal([580, 420], group.Sizes);
        Assert.Equal(group.GroupLength, group.Sizes.Sum());
    }

    [Fact]
    public void ApplyEdgeDrag_LeadingEdgePulledOut_GrowsIntoTheNeighborBehindIt()
    {
        var group = Group(SplitAxis.Horizontal, 1000, 500, 500);

        var applied = LayoutTree.ApplyEdgeDrag(
            group.Children[1],
            new Rect(500, 0, 500, 800),
            new Rect(420, 0, 580, 800));

        Assert.True(applied);
        Assert.Equal([420, 580], group.Sizes);
    }

    [Fact]
    public void ApplyEdgeDrag_LeadingEdgePushedIn_GivesTheDraggedPixelsBack()
    {
        var group = Group(SplitAxis.Horizontal, 1000, 500, 500);

        var applied = LayoutTree.ApplyEdgeDrag(
            group.Children[1],
            new Rect(500, 0, 500, 800),
            new Rect(580, 0, 420, 800));

        Assert.True(applied);
        Assert.Equal([580, 420], group.Sizes);
    }

    /// <summary>
    /// The reported case: two windows split 1/2 - 1/2 across one horizontal group have no boundary
    /// above or below either of them, so a vertical drag has nothing to transfer and the window
    /// goes back to its tile. Only the sideways drag is answerable.
    /// </summary>
    [Theory]
    [InlineData(SplitAxis.Horizontal, 0, 0, 500, 700)]
    [InlineData(SplitAxis.Horizontal, 0, 100, 500, 700)]
    public void ApplyEdgeDrag_NoNeighborOnThatAxis_IsNoOp(
        SplitAxis axis, int x, int y, int width, int height)
    {
        var group = Group(axis, 1000, 500, 500);

        var applied = LayoutTree.ApplyEdgeDrag(
            group.Children[0],
            new Rect(0, 0, 500, 800),
            new Rect(x, y, width, height));

        Assert.False(applied);
        Assert.Equal([500, 500], group.Sizes);
    }

    /// <summary>The work-area border is the same case: nothing on the far side of it to take from.</summary>
    [Fact]
    public void ApplyEdgeDrag_OuterEdge_IsNoOp()
    {
        var group = Group(SplitAxis.Horizontal, 1000, 500, 500);

        var applied = LayoutTree.ApplyEdgeDrag(
            group.Children[0],
            new Rect(0, 0, 500, 800),
            new Rect(-80, 0, 580, 800));

        Assert.False(applied);
        Assert.Equal([500, 500], group.Sizes);
    }

    /// <summary>Add a window above or below and the SAME vertical drag starts moving that boundary.</summary>
    [Fact]
    public void ApplyEdgeDrag_VerticalNeighborUpTheTree_MovesTheOuterBoundary()
    {
        var (root, row) = NestedRowInsideColumn();

        var applied = LayoutTree.ApplyEdgeDrag(
            row.Children[0],
            new Rect(0, 0, 500, 400),
            new Rect(0, 0, 500, 460));

        Assert.True(applied);
        Assert.Equal([460, 340], root.Sizes);
        Assert.Equal([500, 500], row.Sizes);
    }

    /// <summary>A window with a neighbour on both axes answers a corner drag on both at once.</summary>
    [Fact]
    public void ApplyEdgeDrag_CornerWithNeighborsOnBothAxes_AppliesBothBoundaries()
    {
        var (root, row) = NestedRowInsideColumn();

        var applied = LayoutTree.ApplyEdgeDrag(
            row.Children[0],
            new Rect(0, 0, 500, 400),
            new Rect(0, 0, 580, 460));

        Assert.True(applied);
        Assert.Equal([580, 420], row.Sizes);
        Assert.Equal([460, 340], root.Sizes);
    }

    /// <summary>A drag past the floor lands ON the floor rather than being refused outright.</summary>
    [Fact]
    public void ApplyEdgeDrag_DraggedPastTheNeighborsMinimum_TransfersOnlyTheHeadroom()
    {
        var group = Group(SplitAxis.Horizontal, 1000, 500, 500);

        var applied = LayoutTree.ApplyEdgeDrag(
            group.Children[0],
            new Rect(0, 0, 500, 800),
            new Rect(0, 0, 950, 800));

        Assert.True(applied);
        Assert.Equal([900, 100], group.Sizes);
    }

    [Fact]
    public void ApplyEdgeDrag_NeighborAlreadyAtMinimum_IsNoOp()
    {
        var group = Group(SplitAxis.Horizontal, 1000, 900, 100);

        var applied = LayoutTree.ApplyEdgeDrag(
            group.Children[0],
            new Rect(0, 0, 900, 800),
            new Rect(0, 0, 960, 800));

        Assert.False(applied);
        Assert.Equal([900, 100], group.Sizes);
    }

    /// <summary>
    /// A MOVE reports both of an axis's edges travelling the same distance. Read as two independent
    /// boundary drags it would distort the group while the user asked for no size change at all,
    /// so the length is what decides whether an axis was resized.
    /// </summary>
    [Fact]
    public void ApplyEdgeDrag_PureMove_LeavesTheTreeAlone()
    {
        var (root, row) = NestedRowInsideColumn();

        var applied = LayoutTree.ApplyEdgeDrag(
            row.Children[0],
            new Rect(0, 0, 500, 400),
            new Rect(120, 90, 500, 400));

        Assert.False(applied);
        Assert.Equal([500, 500], row.Sizes);
        Assert.Equal([400, 400], root.Sizes);
    }

    [Fact]
    public void ApplyEdgeDrag_MiddleChild_TakesOnlyFromTheSideThatMoved()
    {
        var group = Group(SplitAxis.Horizontal, 900, 300, 300, 300);

        var applied = LayoutTree.ApplyEdgeDrag(
            group.Children[1],
            new Rect(300, 0, 300, 800),
            new Rect(300, 0, 350, 800));

        Assert.True(applied);
        Assert.Equal([300, 350, 250], group.Sizes);
    }

    /// <summary>A column of 500/400 inside a row of 500/500, i.e. one window with both a side and a floor.</summary>
    private static (GroupNode Root, GroupNode Row) NestedRowInsideColumn()
    {
        var root = Group(SplitAxis.Vertical, 800, 400, 400);
        var row = Group(SplitAxis.Horizontal, 1000, 500, 500);
        row.Parent = root;
        root.Children[0] = row;
        return (root, row);
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
