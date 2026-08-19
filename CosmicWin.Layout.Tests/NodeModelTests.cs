using System.Collections.Generic;
using CosmicWin.Layout;

namespace CosmicWin.Layout.Tests;

/// <summary>
/// LE-1: "The engine MUST model layout as Group{orientation, children:[Node], sizes:[ratio]}
/// with Leaf(window) children; one tree root per monitor. MUST be unit-testable with zero
/// Win32 dependency." These tests cover the model shape only (construction, node polymorphism,
/// value semantics) — no tree mutation yet (see LayoutTreeAddChildTests for that).
/// </summary>
public class NodeModelTests
{
    [Fact]
    public void SplitAxis_HorizontalAndVertical_AreDistinctValues()
    {
        Assert.NotEqual(SplitAxis.Horizontal, SplitAxis.Vertical);
    }

    [Fact]
    public void LeafNode_WrapsWindowRef()
    {
        var window = new WindowRef(42);

        var leaf = new LeafNode(window);

        Assert.Equal(window, leaf.Window);
    }

    [Fact]
    public void LeafNode_RecordEquality_IsValueBased()
    {
        var a = new LeafNode(new WindowRef(7));
        var b = new LeafNode(new WindowRef(7));
        var c = new LeafNode(new WindowRef(8));

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void GroupNode_ExposesAxisAndStartsWithEmptyChildrenAndSizes()
    {
        var group = new GroupNode(SplitAxis.Horizontal);

        Assert.Equal(SplitAxis.Horizontal, group.Axis);
        Assert.Empty(group.Children);
        Assert.Empty(group.Sizes);
    }

    [Fact]
    public void LeafNode_And_GroupNode_AreBothNodes()
    {
        Node leaf = new LeafNode(new WindowRef(1));
        Node group = new GroupNode(SplitAxis.Vertical);

        Assert.IsAssignableFrom<Node>(leaf);
        Assert.IsAssignableFrom<Node>(group);
    }

    [Fact]
    public void GroupNode_CanHoldMixedLeafAndNestedGroupChildren()
    {
        // LE-1: children:[Node] — a Group's children can be either Leaf(window) or another Group.
        var group = new GroupNode(SplitAxis.Horizontal) { GroupLength = 1000 };
        var nested = new GroupNode(SplitAxis.Vertical) { GroupLength = 500 };
        var leaf = new LeafNode(new WindowRef(99));

        group.Children.Add(nested);
        group.Children.Add(leaf);

        Assert.Equal(2, group.Children.Count);
        Assert.IsType<GroupNode>(group.Children[0]);
        Assert.IsType<LeafNode>(group.Children[1]);
        Assert.Same(nested, group.Children[0]);
        Assert.Same(leaf, group.Children[1]);
    }
}
