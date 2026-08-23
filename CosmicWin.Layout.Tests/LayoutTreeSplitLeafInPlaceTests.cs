namespace CosmicWin.Layout.Tests;

/// <summary>
/// LE-4's own scenario is explicit: "a new Horizontal-oriented Group REPLACES the Leaf, with both
/// windows side by side" — a new window splits the tile it is placed into. The existing
/// <see cref="LayoutTree.AddChild(LeafNode, WindowRef, int, int)"/> builds that group but leaves it
/// dangling: it re-parents the leaf and returns the group without ever putting the group where the
/// leaf used to be. That is harmless for a ROOT leaf, whose caller assigns <c>Root</c> itself, and
/// silently corrupts the tree for a nested one — the old parent still lists the leaf, so the leaf
/// has two parents and the new group is unreachable.
/// </summary>
public sealed class LayoutTreeSplitLeafInPlaceTests
{
    private static (GroupNode Parent, LeafNode Target, LeafNode Sibling) BuildRowWithTwoLeaves()
    {
        var target = new LeafNode(new WindowRef(new IntPtr(1)));
        var sibling = new LeafNode(new WindowRef(new IntPtr(2)));
        var parent = new GroupNode(SplitAxis.Horizontal) { GroupLength = 900 };
        parent.Children.Add(target);
        parent.Children.Add(sibling);
        parent.Sizes.Add(400);
        parent.Sizes.Add(500);
        target.Parent = parent;
        sibling.Parent = parent;
        return (parent, target, sibling);
    }

    [Fact]
    public void SplitLeafInPlace_NestedLeaf_PutsTheGroupExactlyWhereTheLeafWas()
    {
        var (parent, target, sibling) = BuildRowWithTwoLeaves();

        var group = LayoutTree.SplitLeafInPlace(target, new WindowRef(new IntPtr(3)), 400, 200);

        Assert.Equal(2, parent.Children.Count);
        Assert.Same(group, parent.Children[0]);
        Assert.Same(sibling, parent.Children[1]);
        Assert.Same(parent, group.Parent);
    }

    /// <summary>The group inherits the leaf's slot: nothing else on the row may move because of a split.</summary>
    [Fact]
    public void SplitLeafInPlace_NestedLeaf_LeavesTheSiblingsSlotSizesUntouched()
    {
        var (parent, target, _) = BuildRowWithTwoLeaves();

        LayoutTree.SplitLeafInPlace(target, new WindowRef(new IntPtr(3)), 400, 200);

        Assert.Equal(new[] { 400, 500 }, parent.Sizes);
    }

    /// <summary>The exact corruption the plain AddChild overload leaves behind on a nested leaf.</summary>
    [Fact]
    public void SplitLeafInPlace_NestedLeaf_ReparentsTheLeafUnderTheNewGroup_NotTheOldParent()
    {
        var (parent, target, _) = BuildRowWithTwoLeaves();

        var group = LayoutTree.SplitLeafInPlace(target, new WindowRef(new IntPtr(3)), 400, 200);

        Assert.Same(group, target.Parent);
        Assert.DoesNotContain(target, parent.Children);
        Assert.Contains(target, group.Children);
    }

    [Fact]
    public void SplitLeafInPlace_PutsTheNewWindowAfterTheSplitLeaf()
    {
        var (_, target, _) = BuildRowWithTwoLeaves();
        var arriving = new WindowRef(new IntPtr(3));

        var group = LayoutTree.SplitLeafInPlace(target, arriving, 400, 200);

        Assert.Same(target, group.Children[0]);
        Assert.Equal(arriving, Assert.IsType<LeafNode>(group.Children[1]).Window);
    }

    /// <summary>LE-4: the split axis comes from the region being split, not from the parent's axis.</summary>
    [Theory]
    [InlineData(400, 200, SplitAxis.Horizontal)]
    [InlineData(200, 400, SplitAxis.Vertical)]
    [InlineData(300, 300, SplitAxis.Vertical)]
    public void SplitLeafInPlace_ChoosesTheAxisFromTheSplitRegionsAspectRatio(
        int width, int height, SplitAxis expected)
    {
        var (_, target, _) = BuildRowWithTwoLeaves();

        var group = LayoutTree.SplitLeafInPlace(target, new WindowRef(new IntPtr(3)), width, height);

        Assert.Equal(expected, group.Axis);
    }

    /// <summary>A root leaf has no parent to patch; the caller assigns the returned group as the new root.</summary>
    [Fact]
    public void SplitLeafInPlace_RootLeaf_ReturnsAParentlessGroup()
    {
        var root = new LeafNode(new WindowRef(new IntPtr(1)));

        var group = LayoutTree.SplitLeafInPlace(root, new WindowRef(new IntPtr(2)), 900, 400);

        Assert.Null(group.Parent);
        Assert.Same(root, group.Children[0]);
    }

    /// <summary>Splitting must keep the design's invariant inside the new group as well.</summary>
    [Fact]
    public void SplitLeafInPlace_NewGroupSizesSumToItsLength()
    {
        var (_, target, _) = BuildRowWithTwoLeaves();

        var group = LayoutTree.SplitLeafInPlace(target, new WindowRef(new IntPtr(3)), 401, 200);

        Assert.Equal(group.GroupLength, group.Sizes.Sum());
    }
}
