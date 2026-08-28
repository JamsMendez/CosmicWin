using CosmicWin.Layout;

namespace CosmicWin.Layout.Tests;

/// <summary>
/// LE-2 "Directional focus — tree walk": given a focused Leaf and a <see cref="Direction"/>,
/// ascend from parent to parent until a matching-orientation ancestor with an available sibling
/// boundary is found, or the tree root is reached with no match. Also covers the
/// <see cref="Node.Parent"/> back-reference introduced by this work unit to support the
/// ancestor walk, and the <c>FindMatchingAncestor</c> helper the design/LE-6 states 's
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
    public void NextFocus_Right_IntoNestedGroup_LandsOnItsNearestLeaf()
    {
        // The matching sibling is itself a nested Group -- the walk must descend into it rather than
        // return the Group itself, and land on the leaf touching the boundary crossed. Travelling
        // Right that is the LEADING child, which is why this direction alone never showed the
        // defect its Left/Up counterparts below pin.
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

    [Fact]
    public void NextFocus_Left_IntoNestedGroupOnTheSameAxis_LandsOnItsNearestLeaf()
    {
        // Reported from real use: with A C B on screen, Alt+H from B lands on A and SKIPS C.
        // The sibling to the left is a Group, and the leaf that actually touches the boundary
        // being crossed is its LAST child, not its first. This shape is the ordinary one, not a
        // corner case: a new window splits the FOCUSED leaf, so opening A, opening B, focusing A
        // again and opening C builds exactly this tree.
        var root = new GroupNode(SplitAxis.Horizontal) { GroupLength = 800 };
        var nested = new GroupNode(SplitAxis.Horizontal) { GroupLength = 400 };
        var far = new LeafNode(new WindowRef(1));
        var adjacent = new LeafNode(new WindowRef(2));
        var focused = new LeafNode(new WindowRef(3));
        LayoutTree.AddChild(nested, far, index: 0);
        LayoutTree.AddChild(nested, adjacent, index: 1);
        LayoutTree.AddChild(root, nested, index: 0);
        LayoutTree.AddChild(root, focused, index: 1);

        var result = LayoutTree.NextFocus(Direction.Left, focused);

        Assert.Equal(FocusWalkStatus.Found, result.Status);
        Assert.Same(adjacent, result.Leaf);
    }

    [Fact]
    public void NextFocus_Up_IntoNestedGroupOnTheSameAxis_LandsOnItsNearestLeaf()
    {
        // The same defect on the vertical axis: Alt+K must land on the BOTTOM leaf of the stack
        // above, which is the one sharing the boundary.
        var root = new GroupNode(SplitAxis.Vertical) { GroupLength = 800 };
        var nested = new GroupNode(SplitAxis.Vertical) { GroupLength = 400 };
        var far = new LeafNode(new WindowRef(1));
        var adjacent = new LeafNode(new WindowRef(2));
        var focused = new LeafNode(new WindowRef(3));
        LayoutTree.AddChild(nested, far, index: 0);
        LayoutTree.AddChild(nested, adjacent, index: 1);
        LayoutTree.AddChild(root, nested, index: 0);
        LayoutTree.AddChild(root, focused, index: 1);

        var result = LayoutTree.NextFocus(Direction.Up, focused);

        Assert.Equal(FocusWalkStatus.Found, result.Status);
        Assert.Same(adjacent, result.Leaf);
    }

    [Fact]
    public void NextFocus_Left_IntoDeeplyNestedGroups_KeepsHuggingTheBoundary()
    {
        // The descent is recursive, so the boundary has to be hugged at EVERY level, not just the
        // first one. Layout: A [ C | D ] on the left, focused on the right -- D is the neighbour.
        var root = new GroupNode(SplitAxis.Horizontal) { GroupLength = 900 };
        var nested = new GroupNode(SplitAxis.Horizontal) { GroupLength = 600 };
        var inner = new GroupNode(SplitAxis.Horizontal) { GroupLength = 300 };
        var far = new LeafNode(new WindowRef(1));
        var middle = new LeafNode(new WindowRef(2));
        var adjacent = new LeafNode(new WindowRef(3));
        var focused = new LeafNode(new WindowRef(4));
        LayoutTree.AddChild(inner, middle, index: 0);
        LayoutTree.AddChild(inner, adjacent, index: 1);
        LayoutTree.AddChild(nested, far, index: 0);
        LayoutTree.AddChild(nested, inner, index: 1);
        LayoutTree.AddChild(root, nested, index: 0);
        LayoutTree.AddChild(root, focused, index: 1);

        var result = LayoutTree.NextFocus(Direction.Left, focused);

        Assert.Equal(FocusWalkStatus.Found, result.Status);
        Assert.Same(adjacent, result.Leaf);
    }

    [Fact]
    public void NextFocus_Left_IntoPerpendicularGroup_StillTakesItsFirstChild()
    {
        // Deliberate, and the boundary rule does NOT apply here: the neighbouring group stacks
        // ACROSS the direction travelled, so every one of its children touches the boundary
        // equally. With no focus history to consult, the leading child stays the answer -- pinned
        // so the direction-aware descent below cannot quietly start reversing this case too.
        var root = new GroupNode(SplitAxis.Horizontal) { GroupLength = 800 };
        var nested = new GroupNode(SplitAxis.Vertical) { GroupLength = 600 };
        var top = new LeafNode(new WindowRef(1));
        var bottom = new LeafNode(new WindowRef(2));
        var focused = new LeafNode(new WindowRef(3));
        LayoutTree.AddChild(nested, top, index: 0);
        LayoutTree.AddChild(nested, bottom, index: 1);
        LayoutTree.AddChild(root, nested, index: 0);
        LayoutTree.AddChild(root, focused, index: 1);

        var result = LayoutTree.NextFocus(Direction.Left, focused);

        Assert.Equal(FocusWalkStatus.Found, result.Status);
        Assert.Same(top, result.Leaf);
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

    // --- FindMatchingAncestor helper, shaped for 's ResizeNode reuse ---

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
