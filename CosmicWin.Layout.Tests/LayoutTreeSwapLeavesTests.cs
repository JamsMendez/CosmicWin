using CosmicWin.Layout;

namespace CosmicWin.Layout.Tests;

/// <summary>
/// Dropping one tiled window onto another exchanges their slots. The SHAPE of the tree is
/// untouched -- only which leaf sits in which slot changes.
/// </summary>
public class LayoutTreeSwapLeavesTests
{
    [Fact]
    public void SwapLeaves_Siblings_ExchangesThemAndLeavesTheSizesWithTheSlots()
    {
        var group = Group(SplitAxis.Horizontal, 1000, 700, 300);
        var (a, b) = (group.Children[0], group.Children[1]);

        Assert.True(LayoutTree.SwapLeaves(a, b));

        Assert.Same(b, group.Children[0]);
        Assert.Same(a, group.Children[1]);

        // The sizes belong to the SLOTS, not to the windows: a swap moves what sits in each
        // slot and must not carry the slot's width along with it, or two drops would drift the
        // layout even though nothing was resized.
        Assert.Equal([700, 300], group.Sizes);
    }

    [Fact]
    public void SwapLeaves_AcrossDifferentGroups_ReparentsBoth()
    {
        var (root, row) = NestedRowInsideColumn();
        var insideRow = row.Children[0];
        var bottomOfColumn = root.Children[1];

        Assert.True(LayoutTree.SwapLeaves(insideRow, bottomOfColumn));

        Assert.Same(bottomOfColumn, row.Children[0]);
        Assert.Same(insideRow, root.Children[1]);
        Assert.Same(row, bottomOfColumn.Parent);
        Assert.Same(root, insideRow.Parent);
    }

    /// <summary>The shape is what must survive: same axes, same child counts, same sizes.</summary>
    [Fact]
    public void SwapLeaves_AcrossDifferentGroups_LeavesTheShapeIdentical()
    {
        var (root, row) = NestedRowInsideColumn();

        Assert.True(LayoutTree.SwapLeaves(row.Children[0], root.Children[1]));

        Assert.Equal([400, 400], root.Sizes);
        Assert.Equal([500, 500], row.Sizes);
        Assert.Equal(2, root.Children.Count);
        Assert.Equal(2, row.Children.Count);
    }

    [Fact]
    public void SwapLeaves_TheSameLeafTwice_IsNoOp()
    {
        var group = Group(SplitAxis.Horizontal, 1000, 500, 500);
        var a = group.Children[0];

        Assert.False(LayoutTree.SwapLeaves(a, a));
        Assert.Same(a, group.Children[0]);
    }

    /// <summary>
    /// A node with no parent is the whole tree, so there is no second leaf to exchange it with and
    /// nothing sensible to do. Reported as refused rather than crashing on a null parent.
    /// </summary>
    [Fact]
    public void SwapLeaves_WithARootThatHasNoParent_IsRefused()
    {
        var group = Group(SplitAxis.Horizontal, 1000, 500, 500);
        var lonely = new LeafNode(new WindowRef(99));

        Assert.False(LayoutTree.SwapLeaves(group.Children[0], lonely));
        Assert.Same(group.Children[0], group.Children[0]);
    }

    /// <summary>A whole GROUP can be swapped too -- the operation is about slots, not about leaves.</summary>
    [Fact]
    public void SwapLeaves_ASubtreeAndALeaf_MovesTheWholeSubtree()
    {
        var (root, row) = NestedRowInsideColumn();
        var bottom = root.Children[1];

        Assert.True(LayoutTree.SwapLeaves(row, bottom));

        Assert.Same(bottom, root.Children[0]);
        Assert.Same(row, root.Children[1]);
        Assert.Equal([400, 400], root.Sizes);
    }

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
