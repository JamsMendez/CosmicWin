using CosmicWin.Layout;

namespace CosmicWin.Layout.Tests;

public class LayoutTreeArrangeTests
{
    [Fact]
    public void Arrange_HorizontalGroup_ReturnsLeafGeometryInTreeOrder()
    {
        var root = Group(SplitAxis.Horizontal, 1000, 500, 300, 200);
        var tree = new LayoutTree(root);

        var result = tree.Arrange(new Rect(10, 20, 1000, 600));

        Assert.Equal(
            [
                (new WindowRef(1), new Rect(10, 20, 500, 600)),
                (new WindowRef(2), new Rect(510, 20, 300, 600)),
                (new WindowRef(3), new Rect(810, 20, 200, 600))
            ],
            result);
        Assert.Equal(new Rect(10, 20, 1000, 600), root.LastGeometry);
    }

    [Fact]
    public void Arrange_NestedVerticalGroup_UsesContainingChildRectangle()
    {
        var root = Group(SplitAxis.Horizontal, 1000, 400, 600);
        var nested = Group(SplitAxis.Vertical, 600, 200, 400);
        nested.Parent = root;
        root.Children[1] = nested;
        var tree = new LayoutTree(root);

        var result = tree.Arrange(new Rect(0, 0, 1000, 600));

        Assert.Equal(new Rect(0, 0, 400, 600), result[0].Bounds);
        Assert.Equal(new Rect(400, 0, 600, 200), result[1].Bounds);
        Assert.Equal(new Rect(400, 200, 600, 400), result[2].Bounds);
        Assert.Equal(new Rect(400, 0, 600, 600), nested.LastGeometry);
    }

    [Fact]
    public void Arrange_NonDivisibleLength_AssignsRoundingRemainderToLastChild()
    {
        var root = Group(SplitAxis.Horizontal, 3, 1, 1, 1);

        var result = new LayoutTree(root).Arrange(new Rect(0, 0, 100, 50));

        Assert.Equal([33, 33, 34], root.Sizes);
        Assert.Equal([33, 33, 34], result.Select(item => item.Bounds.Width));
        Assert.Equal(root.GroupLength, root.Sizes.Sum());
    }

    [Fact]
    public void Arrange_AfterToggleAxis_RescalesRatiosAgainstPerpendicularDimension()
    {
        var root = Group(SplitAxis.Horizontal, 1000, 500, 300, 200);
        var tree = new LayoutTree(root);
        tree.Arrange(new Rect(0, 0, 1000, 600));

        Assert.True(LayoutTree.ToggleAxis(root.Children[0]));
        var result = tree.Arrange(new Rect(0, 0, 1000, 600));

        Assert.Equal(SplitAxis.Vertical, root.Axis);
        Assert.Equal(600, root.GroupLength);
        Assert.Equal([300, 180, 120], root.Sizes);
        Assert.Equal([300, 180, 120], result.Select(item => item.Bounds.Height));
        Assert.All(result, item => Assert.Equal(1000, item.Bounds.Width));
    }

    [Fact]
    public void Arrange_ZeroWeightChildren_SharesAvailableLengthAndPreservesInvariant()
    {
        var root = Group(SplitAxis.Vertical, 0, 0, 0, 0);

        new LayoutTree(root).Arrange(new Rect(0, 0, 90, 101));

        Assert.Equal([34, 34, 33], root.Sizes);
        Assert.Equal(101, root.GroupLength);
        Assert.Equal(root.GroupLength, root.Sizes.Sum());
    }

    [Fact]
    public void Arrange_WhenRoundedSharesExceedLength_NeverProducesNegativeGeometry()
    {
        var root = Group(SplitAxis.Horizontal, 6, 1, 1, 1, 1, 1, 1);

        var result = new LayoutTree(root).Arrange(new Rect(0, 0, 4, 10));

        Assert.Equal([1, 1, 1, 1, 0, 0], root.Sizes);
        Assert.All(result, item => Assert.True(item.Bounds.Width >= 0));
        Assert.Equal(root.GroupLength, root.Sizes.Sum());
    }

    [Fact]
    public void Arrange_EmptyGroup_ReturnsNoLeafGeometry()
    {
        var root = new GroupNode(SplitAxis.Horizontal);

        var result = new LayoutTree(root).Arrange(new Rect(5, 6, 700, 400));

        Assert.Empty(result);
        Assert.Equal(new Rect(5, 6, 700, 400), root.LastGeometry);
        Assert.Equal(0, root.GroupLength);
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
