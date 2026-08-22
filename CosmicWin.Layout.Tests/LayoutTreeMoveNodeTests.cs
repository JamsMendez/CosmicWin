using CosmicWin.Layout;

namespace CosmicWin.Layout.Tests;

public class LayoutTreeMoveNodeTests
{
    /// <summary>
    /// Two siblings: a plain swap, sizes carried along. cosmic-comp guards this the same way
    /// (<c>len == 2</c>); with exactly two children a fork would be indistinguishable from a swap
    /// anyway, so the cheaper operation wins.
    /// </summary>
    [Fact]
    public void MoveNode_MatchingAxisWithTwoSiblings_SwapsNodesAndSizes()
    {
        var parent = Group(SplitAxis.Horizontal, 1000, (1, 400), (2, 600));
        var focused = parent.Children[0];

        var moved = LayoutTree.MoveNode(Direction.Right, focused);

        Assert.True(moved);
        Assert.Equal([2, 1], Windows(parent));
        Assert.Equal([600, 400], parent.Sizes);
        Assert.Same(parent, focused.Parent);
        Assert.Equal(parent.GroupLength, parent.Sizes.Sum());
    }

    /// <summary>
    /// REWRITTEN for cosmic-comp parity (2026-08-22). This asserted a SWAP for a three-child group.
    /// Measured against the real COSMIC, three-or-more siblings fork instead: the mover pairs up
    /// with its neighbour inside a new group taking that neighbour's slot.
    /// <para>
    /// The difference is not cosmetic. A swap is an involution -- press the same direction twice
    /// and you are back where you started, so a window can never travel past its neighbour, which
    /// is exactly the dead end the maintainer hit after two presses. The fork turns the walk into a
    /// reversible cycle, which is what COSMIC actually does.
    /// </para>
    /// </summary>
    [Fact]
    public void MoveNode_MatchingAxisWithThreeSiblings_ForksWithTheNeighbour()
    {
        var parent = Group(SplitAxis.Horizontal, 1000,
            (1, 200), (2, 300), (3, 500));
        var focused = parent.Children[1];
        var neighbour = parent.Children[2];

        var moved = LayoutTree.MoveNode(Direction.Right, focused);

        Assert.True(moved);
        Assert.Equal(2, parent.Children.Count);
        Assert.Same(parent.Children[0], Assert.IsType<LeafNode>(parent.Children[0]));

        var fork = Assert.IsType<GroupNode>(parent.Children[1]);
        Assert.Equal(SplitAxis.Horizontal, fork.Axis);
        Assert.Same(parent, fork.Parent);

        // Right -> the mover lands BEFORE the neighbour it travelled into.
        Assert.Equal([2, 3], Windows(fork));
        Assert.Same(fork, focused.Parent);
        Assert.Same(fork, neighbour.Parent);
        Assert.Equal(fork.GroupLength, fork.Sizes.Sum());
        Assert.Equal(parent.GroupLength, parent.Sizes.Sum());
    }

    [Theory]
    [InlineData(Direction.Left, SplitAxis.Horizontal)]
    [InlineData(Direction.Up, SplitAxis.Vertical)]
    public void MoveNode_MatchingAxisAtBoundary_IsNoOp(Direction direction, SplitAxis axis)
    {
        var parent = Group(axis, 1000, (1, 400), (2, 600));
        var before = parent.Children.ToArray();

        var moved = LayoutTree.MoveNode(direction, parent.Children[0]);

        Assert.False(moved);
        Assert.Equal(before, parent.Children);
        Assert.Equal([400, 600], parent.Sizes);
    }

    /// <summary>
    /// REWRITTEN for cosmic-comp parity (2026-08-22). This test used to assert the opposite: that
    /// the focused node was buried in a NEW nested group together with an adjacent sibling, leaving
    /// it one level DEEPER than it started. cosmic-comp's case (1) does the reverse -- the level
    /// itself splits perpendicular, its former contents drop into the nested group, and the focused
    /// node is lifted OUT to take the half the direction points at. That is what makes a window
    /// pushed sideways leave its stack instead of sinking further into it.
    /// </summary>
    [Fact]
    public void MoveNode_OrientationMismatch_LiftsFocusedOutAndNestsTheRest()
    {
        var parent = Group(SplitAxis.Vertical, 900,
            (1, 200), (2, 300), (3, 400));
        var focused = parent.Children[1];
        var stayed = parent.Children[2];

        var moved = LayoutTree.MoveNode(Direction.Right, focused);

        Assert.True(moved);
        Assert.Equal(SplitAxis.Horizontal, parent.Axis);
        Assert.Equal(2, parent.Children.Count);

        // Right -> the focused window takes the TRAILING half; the siblings it left keep the other.
        Assert.Same(focused, parent.Children[1]);
        Assert.Same(parent, focused.Parent);

        var nested = Assert.IsType<GroupNode>(parent.Children[0]);
        Assert.Equal(SplitAxis.Vertical, nested.Axis);
        Assert.Equal([1, 3], Windows(nested));
        Assert.Same(nested, stayed.Parent);
        Assert.Same(parent, nested.Parent);
        Assert.Equal(nested.GroupLength, nested.Sizes.Sum());
        Assert.Equal(parent.GroupLength, parent.Sizes.Sum());
    }

    /// <summary>
    /// REWRITTEN for cosmic-comp parity (2026-08-22). The old name said it all --
    /// <c>UsesPreviousSiblingAndPreservesOrder</c> -- it asserted that a mismatched move IGNORED the
    /// direction pressed and kept the original sibling order. That is precisely the behaviour the
    /// maintainer reported as wrong on real hardware: pressing Left has to send the window LEFT.
    /// cosmic-comp places the node at index 0 for Left/Up and index 1 for Right/Down.
    /// </summary>
    [Fact]
    public void MoveNode_OrientationMismatch_HonoursTheDirectionPressed()
    {
        var parent = Group(SplitAxis.Vertical, 1000,
            (1, 250), (2, 350), (3, 400));
        var focused = parent.Children[2];

        Assert.True(LayoutTree.MoveNode(Direction.Left, focused));

        Assert.Equal(SplitAxis.Horizontal, parent.Axis);

        // Left -> the LEADING half, ahead of everything it left behind.
        Assert.Same(focused, parent.Children[0]);
        Assert.Same(parent, focused.Parent);

        var nested = Assert.IsType<GroupNode>(parent.Children[1]);
        Assert.Equal([1, 2], Windows(nested));
        Assert.Equal(parent.GroupLength, parent.Sizes.Sum());
        Assert.Equal(nested.GroupLength, nested.Sizes.Sum());
    }

    [Fact]
    public void MoveNode_OrientationMismatchWithSoleChild_IsNoOp()
    {
        var parent = Group(SplitAxis.Vertical, 800, (1, 800));
        var focused = parent.Children[0];

        var moved = LayoutTree.MoveNode(Direction.Right, focused);

        Assert.False(moved);
        Assert.Same(focused, Assert.Single(parent.Children));
        Assert.Equal([800], parent.Sizes);
        Assert.Same(parent, focused.Parent);
    }

    [Fact]
    public void MoveNode_ParentlessNode_IsNoOp()
    {
        var focused = new LeafNode(new WindowRef(1));

        Assert.False(LayoutTree.MoveNode(Direction.Right, focused));
        Assert.Null(focused.Parent);
    }

    // --- COSMIC parity: move_current_node's ancestor walk (cosmic-comp/src/shell/layout/tiling/mod.rs:1507) ---

    /// <summary>
    /// Maintainer report, 2026-08-22, verified against the vendored cosmic-comp source: pushing a
    /// window at the EDGE of its group toward the outside must escape the group, not dead-end.
    /// Layout is COSMIC's own example -- one window on the left half, two stacked on the right.
    /// Focus the TOP of the stack and press Up: there is no sibling above it inside the stack, so
    /// the walk must ascend and act at the level above, exactly as
    /// <c>move_current_node</c>'s <c>while let Some(parent) = maybe_parent</c> loop does.
    /// CosmicWin's MoveNode reads only <c>focused.Parent</c> and returns false here.
    /// </summary>
    [Fact]
    public void MoveNode_AtGroupEdge_EscapesToTheAncestorInsteadOfDeadEnding()
    {
        var stack = Group(SplitAxis.Vertical, 500, (2, 250), (3, 250));
        var root = Group(SplitAxis.Horizontal, 1000, (1, 500));
        root.Children.Add(stack);
        root.Sizes.Add(500);
        stack.Parent = root;
        var focused = stack.Children[0];

        Assert.True(
            LayoutTree.MoveNode(Direction.Up, focused),
            "Up from the top of the stack must escape the group via the ancestor walk, not no-op.");
        Assert.NotSame(stack, focused.Parent);
    }

    /// <summary>
    /// The second half of the same report: pushing a window TOWARD a neighbouring group must move
    /// it INTO that group. cosmic-comp does this at mod.rs:1717 --
    /// <c>is_group() &amp;&amp; len == 2</c> then <c>move_node(ToParent(&amp;next_child_id))</c>.
    /// CosmicWin instead swaps the window with the whole group as an opaque unit.
    /// </summary>
    [Fact]
    public void MoveNode_TowardNeighbouringGroup_MovesIntoIt()
    {
        var stack = Group(SplitAxis.Vertical, 500, (2, 250), (3, 250));
        var root = Group(SplitAxis.Horizontal, 1000, (1, 500));
        root.Children.Add(stack);
        root.Sizes.Add(500);
        stack.Parent = root;
        var focused = root.Children[0];

        Assert.True(LayoutTree.MoveNode(Direction.Right, focused));
        Assert.Same(stack, focused.Parent);
    }

    private static GroupNode Group(
        SplitAxis axis,
        int length,
        params (int Window, int Size)[] children)
    {
        var group = new GroupNode(axis) { GroupLength = length };
        foreach (var (window, size) in children)
        {
            var child = new LeafNode(new WindowRef(window)) { Parent = group };
            group.Children.Add(child);
            group.Sizes.Add(size);
        }

        return group;
    }

    private static int[] Windows(GroupNode group) =>
        group.Children.Select(node => Assert.IsType<LeafNode>(node).Window.Handle.ToInt32()).ToArray();
}
