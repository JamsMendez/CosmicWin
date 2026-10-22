using CosmicWin.Layout;

namespace CosmicWin.Layout.Tests;

/// <summary>
/// Dropping one tiled window onto the top or bottom third of another stacks it there: it leaves
/// the slot it had (the group it vacated heals the way a close heals it), and the TARGET's slot
/// splits top/bottom, half each. Nothing outside those two places changes size.
/// </summary>
public class LayoutTreeStackBesideTests
{
    private static readonly Rect Screen = new(0, 0, 1920, 1080);

    /// <summary>
    /// The reported case: <c>[A] [B] [C over D]</c>, D dropped on the lower part of B. B's column
    /// splits in two, and C takes the whole height its group left.
    /// </summary>
    [Fact]
    public void StackBeside_Below_OnTheReportedLayout_SplitsTheTargetsColumnAndCollapsesTheVacatedOne()
    {
        var (tree, a, b, c, d) = ThreeColumnsWithAStackedThird();

        Assert.True(tree.StackBeside(d, b, StackSide.Below));

        var root = Assert.IsType<GroupNode>(tree.Root);
        Assert.Same(a, root.Children[0]);
        var column = Assert.IsType<GroupNode>(root.Children[1]);
        Assert.Equal(SplitAxis.Vertical, column.Axis);
        Assert.Same(b, column.Children[0]);
        Assert.Same(d, column.Children[1]);
        Assert.Same(c, root.Children[2]);

        // No column moved: the new split lives entirely inside B's old slot.
        Assert.Equal([640, 640, 640], root.Sizes);
        Assert.Equal([540, 540], column.Sizes);
        AssertWellFormed(tree);

        var placed = Placed(tree);
        Assert.Equal(new Rect(0, 0, 640, 1080), placed[a]);
        Assert.Equal(new Rect(640, 0, 640, 540), placed[b]);
        Assert.Equal(new Rect(640, 540, 640, 540), placed[d]);
        Assert.Equal(new Rect(1280, 0, 640, 1080), placed[c]);
    }

    [Fact]
    public void StackBeside_Above_OnTheReportedLayout_PutsTheMovedLeafOnTop()
    {
        var (tree, a, b, c, d) = ThreeColumnsWithAStackedThird();

        Assert.True(tree.StackBeside(d, b, StackSide.Above));

        var root = Assert.IsType<GroupNode>(tree.Root);
        var column = Assert.IsType<GroupNode>(root.Children[1]);
        Assert.Same(d, column.Children[0]);
        Assert.Same(b, column.Children[1]);
        AssertWellFormed(tree);

        var placed = Placed(tree);
        Assert.Equal(new Rect(640, 0, 640, 540), placed[d]);
        Assert.Equal(new Rect(640, 540, 640, 540), placed[b]);
        Assert.Equal(new Rect(1280, 0, 640, 1080), placed[c]);
        Assert.Equal(new Rect(0, 0, 640, 1080), placed[a]);
    }

    /// <summary>
    /// A target already inside a top/bottom group gets a new SIBLING rather than a nested group of
    /// its own: <c>V[X, T]</c> does not become <c>V[V[X, D], T]</c>, which would be a level that
    /// means nothing to the focus walk and the moves.
    /// </summary>
    [Fact]
    public void StackBeside_IntoAnExistingVerticalGroup_InsertsASiblingAndSplitsOnlyTheTargetsShare()
    {
        var root = Group(SplitAxis.Horizontal, 1920);
        var a = Leaf(root, 1, 640);
        var column = Group(SplitAxis.Vertical, 1080);
        Attach(root, column, 640);
        var x = Leaf(column, 2, 540);
        var t = Leaf(column, 3, 540);
        var d = Leaf(root, 4, 640);
        var tree = Arranged(root);

        Assert.True(tree.StackBeside(d, x, StackSide.Below));

        var healed = Assert.IsType<GroupNode>(tree.Root);
        Assert.Equal(2, healed.Children.Count);
        Assert.Same(a, healed.Children[0]);
        Assert.Same(column, healed.Children[1]);
        Assert.Equal([x, d, t], column.Children);

        // X's 540 is shared between X and D; T keeps exactly what it had.
        Assert.Equal([270, 270, 540], column.Sizes);
        AssertWellFormed(tree);
    }

    /// <summary>An odd share cannot halve exactly; the sum must still hold to the pixel.</summary>
    [Fact]
    public void StackBeside_AnOddShare_KeepsTheGroupsSumExact()
    {
        var root = Group(SplitAxis.Horizontal, 1920);
        var column = Group(SplitAxis.Vertical, 1080);
        Attach(root, column, 960);
        var x = Leaf(column, 1, 541);
        _ = Leaf(column, 2, 539);
        var moving = Leaf(root, 3, 960);
        var tree = new LayoutTree(root);

        Assert.True(tree.StackBeside(moving, x, StackSide.Above));

        var healed = Assert.IsType<GroupNode>(tree.Root);
        Assert.Equal(1080, healed.Sizes.Sum());
        Assert.Equal(541, healed.Sizes[0] + healed.Sizes[1]);
        Assert.Equal(539, healed.Sizes[2]);
        AssertWellFormed(tree);
    }

    /// <summary>
    /// Reordering inside one column: D dropped on the top third of C, its own neighbour. The group
    /// they share collapses the moment D leaves it and is rebuilt around C -- the tree must come
    /// out well formed rather than holding a group the walk can no longer reach.
    /// </summary>
    [Fact]
    public void StackBeside_WithinTheSameColumn_ReordersAndKeepsTheTreeWellFormed()
    {
        var (tree, a, b, c, d) = ThreeColumnsWithAStackedThird();

        Assert.True(tree.StackBeside(d, c, StackSide.Above));

        var root = Assert.IsType<GroupNode>(tree.Root);
        Assert.Same(a, root.Children[0]);
        Assert.Same(b, root.Children[1]);
        var column = Assert.IsType<GroupNode>(root.Children[2]);
        Assert.Equal(SplitAxis.Vertical, column.Axis);
        Assert.Equal([d, c], column.Children);
        Assert.Equal([640, 640, 640], root.Sizes);
        AssertWellFormed(tree);

        var placed = Placed(tree);
        Assert.Equal(new Rect(1280, 0, 640, 540), placed[d]);
        Assert.Equal(new Rect(1280, 540, 640, 540), placed[c]);
    }

    /// <summary>
    /// The vacated group was the ROOT: two windows stacked, the bottom one dropped on the top one's
    /// upper third. The root collapses to the lone target, which is then split back into a root
    /// group -- the re-seat the static helpers cannot do and the tree has to.
    /// </summary>
    [Fact]
    public void StackBeside_WhenTheVacatedGroupIsTheRoot_ReseatsTheRoot()
    {
        var root = Group(SplitAxis.Vertical, 1080);
        var top = Leaf(root, 1, 540);
        var bottom = Leaf(root, 2, 540);
        var tree = Arranged(root);

        Assert.True(tree.StackBeside(bottom, top, StackSide.Above));

        var reseated = Assert.IsType<GroupNode>(tree.Root);
        Assert.Null(reseated.Parent);
        Assert.Equal([bottom, top], reseated.Children);
        AssertWellFormed(tree);

        var placed = Placed(tree);
        Assert.Equal(new Rect(0, 0, 1920, 540), placed[bottom]);
        Assert.Equal(new Rect(0, 540, 1920, 540), placed[top]);
    }

    [Fact]
    public void StackBeside_OntoItself_IsRefusedAndChangesNothing()
    {
        var (tree, _, b, _, _) = ThreeColumnsWithAStackedThird();
        var root = Assert.IsType<GroupNode>(tree.Root);

        Assert.False(tree.StackBeside(b, b, StackSide.Below));

        Assert.Same(b, root.Children[1]);
        Assert.Equal([640, 640, 640], root.Sizes);
    }

    /// <summary>
    /// A target this tree does not hold is refused BEFORE anything is detached -- refusing after
    /// would leave the moving window out of the layout altogether.
    /// </summary>
    [Fact]
    public void StackBeside_ATargetOutsideThisTree_IsRefusedAndChangesNothing()
    {
        var (tree, _, _, c, d) = ThreeColumnsWithAStackedThird();
        var stranger = new LeafNode(new WindowRef(99));

        Assert.False(tree.StackBeside(d, stranger, StackSide.Below));

        var column = Assert.IsType<GroupNode>(Assert.IsType<GroupNode>(tree.Root).Children[2]);
        Assert.Equal([c, d], column.Children);
        Assert.Null(stranger.Parent);
    }

    /// <summary>A node with no parent IS the whole tree: there is nothing beside it to stack with.</summary>
    [Fact]
    public void StackBeside_TheRootItself_IsRefused()
    {
        var lonely = new LeafNode(new WindowRef(1));
        var tree = new LayoutTree(lonely);

        Assert.False(tree.StackBeside(lonely, new LeafNode(new WindowRef(2)), StackSide.Above));
        Assert.Same(lonely, tree.Root);
    }

    /// <summary>A group cannot be stacked beside a window inside itself; there would be no slot left.</summary>
    [Fact]
    public void StackBeside_AGroupOntoOneOfItsOwnLeaves_IsRefused()
    {
        var (tree, _, _, c, _) = ThreeColumnsWithAStackedThird();
        var column = (GroupNode)c.Parent!;

        Assert.False(tree.StackBeside(column, c, StackSide.Above));
        Assert.Same(column, Assert.IsType<GroupNode>(tree.Root).Children[2]);
    }

    private static (LayoutTree Tree, LeafNode A, LeafNode B, LeafNode C, LeafNode D) ThreeColumnsWithAStackedThird()
    {
        var root = Group(SplitAxis.Horizontal, 1920);
        var a = Leaf(root, 1, 640);
        var b = Leaf(root, 2, 640);
        var column = Group(SplitAxis.Vertical, 1080);
        Attach(root, column, 640);
        var c = Leaf(column, 3, 540);
        var d = Leaf(column, 4, 540);
        return (Arranged(root), a, b, c, d);
    }

    /// <summary>Arranged once, as the real desktop would have been before anything was dragged.</summary>
    private static LayoutTree Arranged(GroupNode root)
    {
        var tree = new LayoutTree(root);
        tree.Arrange(Screen);
        return tree;
    }

    private static Dictionary<LeafNode, Rect> Placed(LayoutTree tree)
    {
        tree.Arrange(Screen);
        var placed = new Dictionary<LeafNode, Rect>(ReferenceEqualityComparer.Instance);
        Collect(tree.Root!, placed);
        return placed;
    }

    private static void Collect(Node node, Dictionary<LeafNode, Rect> placed)
    {
        if (node is LeafNode leaf)
        {
            placed[leaf] = leaf.LastGeometry;
            return;
        }

        foreach (var child in ((GroupNode)node).Children)
        {
            Collect(child, placed);
        }
    }

    /// <summary>
    /// What "valid tree" means here: every child points back at the group that lists it, every
    /// group has one size per child summing to its length, and no group is left empty or holding a
    /// single child -- the two shapes <see cref="LayoutTree.Prune"/> exists to heal.
    /// </summary>
    private static void AssertWellFormed(LayoutTree tree)
    {
        Assert.NotNull(tree.Root);
        Assert.Null(tree.Root!.Parent);
        Check(tree.Root);

        static void Check(Node node)
        {
            if (node is not GroupNode group)
            {
                return;
            }

            Assert.True(group.Children.Count >= 2, "a group must hold at least two children");
            Assert.Equal(group.Children.Count, group.Sizes.Count);
            Assert.Equal(group.GroupLength, group.Sizes.Sum());
            foreach (var child in group.Children)
            {
                Assert.Same(group, child.Parent);
                Check(child);
            }
        }
    }

    private static GroupNode Group(SplitAxis axis, int length) => new(axis) { GroupLength = length };

    private static LeafNode Leaf(GroupNode parent, nint handle, int size)
    {
        var leaf = new LeafNode(new WindowRef(handle));
        Attach(parent, leaf, size);
        return leaf;
    }

    private static void Attach(GroupNode parent, Node child, int size)
    {
        child.Parent = parent;
        parent.Children.Add(child);
        parent.Sizes.Add(size);
    }
}
