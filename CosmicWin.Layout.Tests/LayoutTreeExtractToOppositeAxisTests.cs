using CosmicWin.Layout;

namespace CosmicWin.Layout.Tests;

/// <summary>
/// Taking one child out of a group and standing it BESIDE its former siblings, on the other axis.
/// </summary>
/// <remarks>
/// <para>
/// The shape a window with a minimum size needs. Three windows stacked into rows on a 1376-tall
/// field get 453 each; a window that will not go under 772 cannot be one of them, and the only
/// answers used to be ejecting it or squashing everyone. There is a third: give it its own branch.
/// </para>
/// <para>
/// <c>[A, B, C]</c> stacked, with B constrained, becomes <c>[[A, C] stacked, B] side by side</c> --
/// A and C get the stacking that was asked for, and B gets the full height it needs. Nobody leaves
/// the tree and nobody is squashed below what the arrangement can afford.
/// </para>
/// <para>
/// Pure surgery. It knows nothing about minimum sizes -- the caller decides WHO needs extracting,
/// because that is measured from real windows and this project has never seen one.
/// </para>
/// </remarks>
public class LayoutTreeExtractToOppositeAxisTests
{
    private static (LayoutTree Tree, GroupNode Group, LeafNode A, LeafNode B, LeafNode C) ThreeStacked()
    {
        var group = new GroupNode(SplitAxis.Vertical) { GroupLength = 1200 };
        var a = new LeafNode(new WindowRef(1));
        var b = new LeafNode(new WindowRef(2));
        var c = new LeafNode(new WindowRef(3));
        LayoutTree.AddChild(group, a, index: 0);
        LayoutTree.AddChild(group, b, index: 1);
        LayoutTree.AddChild(group, c, index: 2);
        return (new LayoutTree(group), group, a, b, c);
    }

    [Fact]
    public void Extract_StandsTheLeafBesideAGroupOfItsFormerSiblings()
    {
        var (tree, group, a, b, c) = ThreeStacked();

        Assert.True(tree.ExtractToOppositeAxis(b));

        var root = Assert.IsType<GroupNode>(tree.Root);
        Assert.Equal(SplitAxis.Horizontal, root.Axis);
        Assert.Equal(2, root.Children.Count);
        Assert.Same(group, root.Children[0]);
        Assert.Same(b, root.Children[1]);

        // The siblings keep the axis they were given, which is what the toggle asked for.
        Assert.Equal(SplitAxis.Vertical, group.Axis);
        Assert.Equal([a, c], group.Children);
    }

    [Fact]
    public void Extract_RewiresEveryParentPointer()
    {
        var (tree, group, _, b, _) = ThreeStacked();

        Assert.True(tree.ExtractToOppositeAxis(b));

        var root = Assert.IsType<GroupNode>(tree.Root);
        Assert.Null(root.Parent);
        Assert.Same(root, group.Parent);
        Assert.Same(root, b.Parent);
    }

    /// <summary>
    /// The point of the whole operation, in geometry: the extracted window gets the FULL extent of
    /// the axis it could not fit on.
    /// </summary>
    [Fact]
    public void Extract_GivesTheLeafTheWholeOfTheAxisItCouldNotFitOn()
    {
        var (tree, _, _, b, _) = ThreeStacked();
        var field = new Rect(0, 0, 1600, 1200);

        var before = tree.Arrange(field).Single(placed => placed.Window.Equals(b.Window)).Bounds;
        Assert.Equal(400, before.Height);

        Assert.True(tree.ExtractToOppositeAxis(b));
        var after = tree.Arrange(field).Single(placed => placed.Window.Equals(b.Window)).Bounds;

        Assert.Equal(1200, after.Height);
    }

    /// <summary>
    /// With only two windows there is nothing to stand beside, and the collapse says so: the result
    /// is the pair on the other axis, which is the toggle undone.
    /// </summary>
    /// <remarks>
    /// Not a special case in the code, and deliberately not one. <c>Prune</c> already collapses a
    /// group down to one child, so the general operation lands on the honest answer by itself --
    /// a window that cannot share the new axis with ONE neighbour cannot be given a branch that
    /// helps, and keeping the old orientation is the best the layout can do.
    /// </remarks>
    [Fact]
    public void Extract_FromAPair_LeavesThePairOnTheOtherAxis()
    {
        var group = new GroupNode(SplitAxis.Vertical) { GroupLength = 1200 };
        var a = new LeafNode(new WindowRef(1));
        var b = new LeafNode(new WindowRef(2));
        LayoutTree.AddChild(group, a, index: 0);
        LayoutTree.AddChild(group, b, index: 1);
        var tree = new LayoutTree(group);

        Assert.True(tree.ExtractToOppositeAxis(b));

        var root = Assert.IsType<GroupNode>(tree.Root);
        Assert.Equal(SplitAxis.Horizontal, root.Axis);
        Assert.Equal([a, b], root.Children);
    }

    /// <summary>Nested, so the wrapper has to take the group's slot rather than the root.</summary>
    [Fact]
    public void Extract_UnderAGrandparent_TakesTheGroupsSlotInPlace()
    {
        var (_, group, _, b, _) = ThreeStacked();
        var outer = new GroupNode(SplitAxis.Horizontal) { GroupLength = 2000 };
        var other = new LeafNode(new WindowRef(9));
        LayoutTree.AddChild(outer, other, index: 0);
        LayoutTree.AddChild(outer, group, index: 1);
        var tree = new LayoutTree(outer);

        Assert.True(tree.ExtractToOppositeAxis(b));

        Assert.Same(outer, tree.Root);
        Assert.Same(other, outer.Children[0]);
        var wrapper = Assert.IsType<GroupNode>(outer.Children[1]);
        Assert.Same(outer, wrapper.Parent);
        Assert.Same(group, wrapper.Children[0]);
        Assert.Same(b, wrapper.Children[1]);
    }

    /// <summary>A leaf with no siblings has nowhere to be extracted TO.</summary>
    [Fact]
    public void Extract_ARootLeaf_ChangesNothing()
    {
        var only = new LeafNode(new WindowRef(1));
        var tree = new LayoutTree(only);

        Assert.False(tree.ExtractToOppositeAxis(only));

        Assert.Same(only, tree.Root);
    }
}
