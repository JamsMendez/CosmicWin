using CosmicWin.Layout;

namespace CosmicWin.Layout.Tests;

public class LayoutTreeMoveNodeTests
{
    [Fact]
    public void MoveNode_MatchingAxis_SwapsNodesAndSizes()
    {
        var parent = Group(SplitAxis.Horizontal, 1000,
            (1, 200), (2, 300), (3, 500));
        var focused = parent.Children[1];

        var moved = LayoutTree.MoveNode(Direction.Right, focused);

        Assert.True(moved);
        Assert.Equal([1, 3, 2], Windows(parent));
        Assert.Equal([200, 500, 300], parent.Sizes);
        Assert.Same(parent, focused.Parent);
        Assert.Equal(parent.GroupLength, parent.Sizes.Sum());
    }

    [Theory]
    [InlineData(Direction.Left, SplitAxis.Horizontal)]
    [InlineData(Direction.Up, SplitAxis.Vertical)]
    public void MoveNode_MatchingAxisAtBoundary_IsNoOp(Direction direction, SplitAxis axis)
    {
        var parent = Group(axis, 1000, (1, 400), (2, 600));
        var before = parent.Children.ToArray();

        var moved = LayoutTree.MoveNode(direction, parent.Children[0]);

        Assert.False(moved);
        Assert.Equal(before, parent.Children);
        Assert.Equal([400, 600], parent.Sizes);
    }

    [Fact]
    public void MoveNode_OrientationMismatch_ReparentsAdjacentNodesIntoRequestedAxisGroup()
    {
        var parent = Group(SplitAxis.Vertical, 900,
            (1, 200), (2, 300), (3, 400));
        var focused = parent.Children[1];
        var adjacent = parent.Children[2];

        var moved = LayoutTree.MoveNode(Direction.Right, focused);

        Assert.True(moved);
        Assert.Equal(2, parent.Children.Count);
        Assert.Equal([200, 700], parent.Sizes);
        var nested = Assert.IsType<GroupNode>(parent.Children[1]);
        Assert.Equal(SplitAxis.Horizontal, nested.Axis);
        Assert.Equal([2, 3], Windows(nested));
        Assert.Same(nested, focused.Parent);
        Assert.Same(nested, adjacent.Parent);
        Assert.Same(parent, nested.Parent);
        Assert.Equal(nested.GroupLength, nested.Sizes.Sum());
        Assert.Equal(parent.GroupLength, parent.Sizes.Sum());
    }

    [Fact]
    public void MoveNode_OrientationMismatchAtLastChild_UsesPreviousSiblingAndPreservesOrder()
    {
        var parent = Group(SplitAxis.Vertical, 1000,
            (1, 250), (2, 350), (3, 400));
        var focused = parent.Children[2];

        Assert.True(LayoutTree.MoveNode(Direction.Left, focused));

        Assert.Equal([250, 750], parent.Sizes);
        var nested = Assert.IsType<GroupNode>(parent.Children[1]);
        Assert.Equal([2, 3], Windows(nested));
        Assert.Same(nested, focused.Parent);
    }

    [Fact]
    public void MoveNode_OrientationMismatchWithSoleChild_IsNoOp()
    {
        var parent = Group(SplitAxis.Vertical, 800, (1, 800));
        var focused = parent.Children[0];

        var moved = LayoutTree.MoveNode(Direction.Right, focused);

        Assert.False(moved);
        Assert.Same(focused, Assert.Single(parent.Children));
        Assert.Equal([800], parent.Sizes);
        Assert.Same(parent, focused.Parent);
    }

    [Fact]
    public void MoveNode_ParentlessNode_IsNoOp()
    {
        var focused = new LeafNode(new WindowRef(1));

        Assert.False(LayoutTree.MoveNode(Direction.Right, focused));
        Assert.Null(focused.Parent);
    }

    private static GroupNode Group(
        SplitAxis axis,
        int length,
        params (int Window, int Size)[] children)
    {
        var group = new GroupNode(axis) { GroupLength = length };
        foreach (var (window, size) in children)
        {
            var child = new LeafNode(new WindowRef(window)) { Parent = group };
            group.Children.Add(child);
            group.Sizes.Add(size);
        }

        return group;
    }

    private static int[] Windows(GroupNode group) =>
        group.Children.Select(node => Assert.IsType<LeafNode>(node).Window.Handle.ToInt32()).ToArray();
}
