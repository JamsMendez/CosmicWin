using CosmicWin.Layout;

namespace CosmicWin.Layout.Tests;

/// <summary>
/// LE-3 "Orientation toggle": Alt+O flips the focused node's immediate parent group's
/// orientation in place, proportionally re-flowing its existing children while preserving their
/// size ratios and order. Does not affect future-split heuristics (LE-4, <c>ChooseSplitAxis</c>
/// — untouched and unrelated).
/// </summary>
public class LayoutTreeToggleAxisTests
{
    [Fact]
    public void ToggleAxis_FlipsHorizontalParentToVertical()
    {
        // LE-3 scenario: Horizontal parent group with 3 children at ratios [0.5, 0.3, 0.2].
        var group = new GroupNode(SplitAxis.Horizontal) { GroupLength = 1000 };
        var a = new LeafNode(new WindowRef(1));
        var b = new LeafNode(new WindowRef(2));
        var c = new LeafNode(new WindowRef(3));
        LayoutTree.AddChild(group, a, index: 0);
        LayoutTree.AddChild(group, b, index: 1);
        LayoutTree.AddChild(group, c, index: 2);
        group.Sizes[0] = 500;
        group.Sizes[1] = 300;
        group.Sizes[2] = 200;

        LayoutTree.ToggleAxis(a);

        Assert.Equal(SplitAxis.Vertical, group.Axis);
    }

    [Fact]
    public void ToggleAxis_PreservesChildrenOrderAndSizeRatios()
    {
        var group = new GroupNode(SplitAxis.Horizontal) { GroupLength = 1000 };
        var a = new LeafNode(new WindowRef(1));
        var b = new LeafNode(new WindowRef(2));
        var c = new LeafNode(new WindowRef(3));
        LayoutTree.AddChild(group, a, index: 0);
        LayoutTree.AddChild(group, b, index: 1);
        LayoutTree.AddChild(group, c, index: 2);
        group.Sizes[0] = 500;
        group.Sizes[1] = 300;
        group.Sizes[2] = 200;

        LayoutTree.ToggleAxis(a);

        Assert.Same(a, group.Children[0]);
        Assert.Same(b, group.Children[1]);
        Assert.Same(c, group.Children[2]);
        Assert.Equal([500, 300, 200], group.Sizes);
    }

    [Fact]
    public void ToggleAxis_FlipsVerticalParentToHorizontal()
    {
        var group = new GroupNode(SplitAxis.Vertical) { GroupLength = 600 };
        var a = new LeafNode(new WindowRef(1));
        var b = new LeafNode(new WindowRef(2));
        LayoutTree.AddChild(group, a, index: 0);
        LayoutTree.AddChild(group, b, index: 1);

        LayoutTree.ToggleAxis(a);

        Assert.Equal(SplitAxis.Horizontal, group.Axis);
    }

    [Fact]
    public void ToggleAxis_TwiceReturnsToOriginalAxis()
    {
        var group = new GroupNode(SplitAxis.Horizontal) { GroupLength = 800 };
        var a = new LeafNode(new WindowRef(1));
        LayoutTree.AddChild(group, a, index: 0);

        LayoutTree.ToggleAxis(a);
        LayoutTree.ToggleAxis(a);

        Assert.Equal(SplitAxis.Horizontal, group.Axis);
    }

    [Fact]
    public void ToggleAxis_ReturnsTrueWhenParentExists()
    {
        var group = new GroupNode(SplitAxis.Horizontal) { GroupLength = 800 };
        var a = new LeafNode(new WindowRef(1));
        LayoutTree.AddChild(group, a, index: 0);

        Assert.True(LayoutTree.ToggleAxis(a));
    }

    [Fact]
    public void ToggleAxis_NodeWithNoParent_IsNoOpAndReturnsFalse()
    {
        // A tree root (or standalone node) has no immediate parent group to flip.
        var root = new LeafNode(new WindowRef(1));

        var result = LayoutTree.ToggleAxis(root);

        Assert.False(result);
    }

    [Fact]
    public void ToggleAxis_DoesNotChangeChooseSplitAxisHeuristic()
    {
        // LE-3: toggling MUST NOT pre-select orientation for future splits (LE-4's heuristic is
        // a pure function of aspect ratio and is entirely unrelated to any group's current Axis).
        var group = new GroupNode(SplitAxis.Horizontal) { GroupLength = 1000 };
        var a = new LeafNode(new WindowRef(1));
        LayoutTree.AddChild(group, a, index: 0);

        LayoutTree.ToggleAxis(a);

        Assert.Equal(SplitAxis.Horizontal, LayoutTree.ChooseSplitAxis(1920, 1080));
        Assert.Equal(SplitAxis.Vertical, LayoutTree.ChooseSplitAxis(1080, 1920));
    }
}
