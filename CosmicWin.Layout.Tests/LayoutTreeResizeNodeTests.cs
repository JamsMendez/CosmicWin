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
