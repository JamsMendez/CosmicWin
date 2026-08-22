namespace CosmicWin.Layout.Tests;

/// <summary>
/// Closing a window must heal the tree it leaves behind. Measured on real hardware 2026-08-22, right
/// after new windows started splitting the focused tile: closing one left its space blank instead of
/// letting the survivors expand into it.
///
/// The mechanism is precise. Removing the last child of a nested group leaves that group with zero
/// children, and <c>ArrangeNode</c> answers an empty group by zeroing its length and returning — but
/// the PARENT still reserves a slot and a size for it, so the region is claimed and nothing is drawn
/// there. A flat tree never showed this: there was only the root group, and its own
/// <c>RemoveChild</c> redistributes. Nesting is what exposed it.
///
/// A group left with exactly ONE child is the quieter half of the same problem: it draws correctly,
/// but it is a level that no longer means anything, and LE-2's tree walk and LE-5's moves both count
/// levels. Both cases are healed here.
/// </summary>
public sealed class LayoutTreePruneTests
{
    private static LeafNode Leaf(int handle) => new(new WindowRef(new IntPtr(handle)));

    private static GroupNode Group(SplitAxis axis, int length, params (Node Child, int Size)[] children)
    {
        var group = new GroupNode(axis) { GroupLength = length };
        foreach (var (child, size) in children)
        {
            group.Children.Add(child);
            group.Sizes.Add(size);
            child.Parent = group;
        }

        return group;
    }

    /// <summary>The reported bug, at its root: the emptied group must leave, and its space must go back to the row.</summary>
    [Fact]
    public void Prune_EmptiedNestedGroup_IsRemovedAndItsSpaceRedistributed()
    {
        var kept = Leaf(1);
        var doomed = Leaf(2);
        var nested = Group(SplitAxis.Vertical, 500, (doomed, 500));
        var root = Group(SplitAxis.Horizontal, 900, (kept, 400), (nested, 500));
        var tree = new LayoutTree(root);

        LayoutTree.RemoveChild(nested, 0);
        LayoutTree.Prune(tree, nested);

        Assert.Single(root.Children);
        Assert.Same(kept, root.Children[0]);
        Assert.Equal(900, root.Sizes.Sum());
    }

    /// <summary>A group down to one child is a level that no longer means anything; the child takes its slot.</summary>
    [Fact]
    public void Prune_NestedGroupWithOneChildLeft_CollapsesIntoThatChild()
    {
        var other = Leaf(1);
        var survivor = Leaf(2);
        var doomed = Leaf(3);
        var nested = Group(SplitAxis.Vertical, 500, (survivor, 250), (doomed, 250));
        var root = Group(SplitAxis.Horizontal, 900, (other, 400), (nested, 500));
        var tree = new LayoutTree(root);

        LayoutTree.RemoveChild(nested, 1);
        LayoutTree.Prune(tree, nested);

        Assert.Equal(2, root.Children.Count);
        Assert.Same(survivor, root.Children[1]);
        Assert.Same(root, survivor.Parent);
        Assert.Equal(new[] { 400, 500 }, root.Sizes);
    }

    /// <summary>Emptying can cascade: one close may leave a chain of hollow groups above it.</summary>
    [Fact]
    public void Prune_CascadesUpwardThroughSeveralHollowLevels()
    {
        var kept = Leaf(1);
        var doomed = Leaf(2);
        var innermost = Group(SplitAxis.Horizontal, 250, (doomed, 250));
        var middle = Group(SplitAxis.Vertical, 500, (innermost, 500));
        var root = Group(SplitAxis.Horizontal, 900, (kept, 400), (middle, 500));
        var tree = new LayoutTree(root);

        LayoutTree.RemoveChild(innermost, 0);
        LayoutTree.Prune(tree, innermost);

        Assert.Same(kept, tree.Root);
        Assert.Null(kept.Parent);
    }

    [Fact]
    public void Prune_EmptiedRootGroup_ClearsTheRoot()
    {
        var doomed = Leaf(1);
        var root = Group(SplitAxis.Horizontal, 900, (doomed, 900));
        var tree = new LayoutTree(root);

        LayoutTree.RemoveChild(root, 0);
        LayoutTree.Prune(tree, root);

        Assert.Null(tree.Root);
    }

    [Fact]
    public void Prune_RootGroupWithOneChildLeft_PromotesThatChildToRoot()
    {
        var survivor = Leaf(1);
        var doomed = Leaf(2);
        var root = Group(SplitAxis.Horizontal, 900, (survivor, 450), (doomed, 450));
        var tree = new LayoutTree(root);

        LayoutTree.RemoveChild(root, 1);
        LayoutTree.Prune(tree, root);

        Assert.Same(survivor, tree.Root);
        Assert.Null(survivor.Parent);
    }

    /// <summary>A group that still holds two or more children is doing its job; pruning must not touch it.</summary>
    [Fact]
    public void Prune_GroupStillHoldingTwoChildren_IsLeftAlone()
    {
        var first = Leaf(1);
        var second = Leaf(2);
        var third = Leaf(3);
        var root = Group(SplitAxis.Horizontal, 900, (first, 300), (second, 300), (third, 300));
        var tree = new LayoutTree(root);

        LayoutTree.RemoveChild(root, 2);
        LayoutTree.Prune(tree, root);

        Assert.Same(root, tree.Root);
        Assert.Equal(2, root.Children.Count);
        Assert.Equal(900, root.Sizes.Sum());
    }
}
