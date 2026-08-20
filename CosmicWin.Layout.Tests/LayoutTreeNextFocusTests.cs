using CosmicWin.Layout;

namespace CosmicWin.Layout.Tests;

/// <summary>
/// LE-2 "Directional focus — tree walk": given a focused Leaf and a <see cref="Direction"/>,
/// ascend from parent to parent until a matching-orientation ancestor with an available sibling
/// boundary is found, or the tree root is reached with no match. Also covers the
/// <see cref="Node.Parent"/> back-reference introduced by this work unit (WU5) to support the
/// ancestor walk, and the <c>FindMatchingAncestor</c> helper design D3/LE-6 states WU6's
/// <c>ResizeNode</c> is meant to reuse.
/// </summary>
public class LayoutTreeNextFocusTests
{
    // --- Parent wiring (prerequisite for the ancestor walk; new AddChild/RemoveChild behavior) ---

    [Fact]
    public void AddChild_ToGroup_SetsChildParentReference()
    {
        var group = new GroupNode(SplitAxis.Horizontal) { GroupLength = 800 };
        var child = new LeafNode(new WindowRef(1));

        LayoutTree.AddChild(group, child, index: 0);

        Assert.Same(group, child.Parent);
    }

    [Fact]
    public void AddChild_SplittingLeaf_SetsParentOnBothOriginalAndNewLeaf()
    {
        var existingLeaf = new LeafNode(new WindowRef(1));

        var group = LayoutTree.AddChild(existingLeaf, new WindowRef(2), regionWidth: 1920, regionHeight: 1080);

        Assert.Same(group, existingLeaf.Parent);
        Assert.Same(group, group.Children[1].Parent);
    }

    [Fact]
    public void RemoveChild_ClearsRemovedNodeParentReference()
    {
        var group = new GroupNode(SplitAxis.Horizontal) { GroupLength = 800 };
        var first = new LeafNode(new WindowRef(1));
        var second = new LeafNode(new WindowRef(2));
        LayoutTree.AddChild(group, first, index: 0);
        LayoutTree.AddChild(group, second, index: 1);

        LayoutTree.RemoveChild(group, index: 1);

        Assert.Null(second.Parent);
    }

    // --- LE-2 Scenario: Sibling in matching orientation ---

    [Fact]
    public void NextFocus_HorizontalParentWithRightSibling_MovesToSiblingLeaf()
    {
        var group = new GroupNode(SplitAxis.Horizontal) { GroupLength = 800 };
        var focused = new LeafNode(new WindowRef(1));
        var sibling = new LeafNode(new WindowRef(2));
        LayoutTree.AddChild(group, focused, index: 0);
        LayoutTree.AddChild(group, sibling, index: 1);

        var result = LayoutTree.NextFocus(Direction.Right, focused);

        Assert.Equal(FocusWalkStatus.Found, result.Status);
        Assert.Same(sibling, result.Leaf);
    }

    [Fact]
    public void NextFocus_DescendsIntoNestedGroupToFindFirstLeaf()
    {
        // The matching sibling is itself a nested Group — the walk must descend via depth-first
        // traversal to that subtree's first leaf, not return the Group itself.
        var root = new GroupNode(SplitAxis.Horizontal) { GroupLength = 800 };
        var focused = new LeafNode(new WindowRef(1));
        var nested = new GroupNode(SplitAxis.Vertical) { GroupLength = 600 };
        var nestedFirstLeaf = new LeafNode(new WindowRef(2));
        var nestedSecondLeaf = new LeafNode(new WindowRef(3));
        LayoutTree.AddChild(nested, nestedFirstLeaf, index: 0);
        LayoutTree.AddChild(nested, nestedSecondLeaf, index: 1);
        LayoutTree.AddChild(root, focused, index: 0);
        LayoutTree.AddChild(root, nested, index: 1);

        var result = LayoutTree.NextFocus(Direction.Right, focused);

        Assert.Equal(FocusWalkStatus.Found, result.Status);
        Assert.Same(nestedFirstLeaf, result.Leaf);
    }

    // --- LE-2 Scenario: Orientation mismatch walks up ---

    [Fact]
    public void NextFocus_VerticalParentNoHorizontalSibling_AscendsToGrandparent()
    {
        var root = new GroupNode(SplitAxis.Horizontal) { GroupLength = 800 };
        var subgroup = new GroupNode(SplitAxis.Vertical) { GroupLength = 600 };
        var focused = new LeafNode(new WindowRef(1));
        var below = new LeafNode(new WindowRef(2));
        var otherLeaf = new LeafNode(new WindowRef(3));
        LayoutTree.AddChild(subgroup, focused, index: 0);
        LayoutTree.AddChild(subgroup, below, index: 1);
        LayoutTree.AddChild(root, subgroup, index: 0);
        LayoutTree.AddChild(root, otherLeaf, index: 1);

        var result = LayoutTree.NextFocus(Direction.Right, focused);

        Assert.Equal(FocusWalkStatus.Found, result.Status);
        Assert.Same(otherLeaf, result.Leaf);
    }

    // --- LE-2 Scenario: Root reached, no match ---

    [Fact]
    public void NextFocus_RootReachedWithNoMatch_ReturnsNoMatch()
    {
        var root = new GroupNode(SplitAxis.Horizontal) { GroupLength = 800 };
        var only = new LeafNode(new WindowRef(1));
        LayoutTree.AddChild(root, only, index: 0);

        var result = LayoutTree.NextFocus(Direction.Left, only);

        Assert.Equal(FocusWalkStatus.NoMatch, result.Status);
        Assert.Null(result.Leaf);
    }

    [Fact]
    public void NextFocus_NoMatchResult_IsDistinctFromFoundResult()
    {
        // MM-5 (later work unit) needs to distinguish "fell through to monitor switching"
        // (NoMatch) from a successful focus move (Found) via an explicit status, not by
        // null-checking the leaf as an implicit error signal.
        Assert.NotEqual(FocusWalkStatus.Found, FocusResult.NoMatch.Status);
    }

    // --- FindMatchingAncestor helper, shaped for WU6's ResizeNode reuse (design D3/LE-6) ---

    [Fact]
    public void FindMatchingAncestor_ReturnsMatchingAncestorAndChildIndex()
    {
        var root = new GroupNode(SplitAxis.Horizontal) { GroupLength = 800 };
        var focused = new LeafNode(new WindowRef(1));
        var sibling = new LeafNode(new WindowRef(2));
        LayoutTree.AddChild(root, focused, index: 0);
        LayoutTree.AddChild(root, sibling, index: 1);

        var match = LayoutTree.FindMatchingAncestor(Direction.Right, focused);

        Assert.NotNull(match);
        Assert.Same(root, match!.Value.Ancestor);
        Assert.Equal(0, match.Value.ChildIndex);
    }

    [Fact]
    public void FindMatchingAncestor_NoMatchAvailable_ReturnsNull()
    {
        var root = new GroupNode(SplitAxis.Horizontal) { GroupLength = 800 };
        var only = new LeafNode(new WindowRef(1));
        LayoutTree.AddChild(root, only, index: 0);

        var match = LayoutTree.FindMatchingAncestor(Direction.Left, only);

        Assert.Null(match);
    }
}
