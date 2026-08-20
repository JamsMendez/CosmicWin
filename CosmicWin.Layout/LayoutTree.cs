namespace CosmicWin.Layout;

/// <summary>
/// Tree operations for the tiling layout engine. This work unit (WU4) covers only node
/// construction and <see cref="AddChild(GroupNode,Node,int)"/> — see WU5/WU6 (design D3) for
/// <c>RemoveChild</c>, <c>NextFocus</c>, <c>ToggleAxis</c>, <c>MoveNode</c>, <c>ResizeNode</c>,
/// and <c>Arrange</c>.
/// </summary>
public sealed class LayoutTree
{
    public Node? Root { get; set; }

    /// <summary>
    /// LE-4 split-orientation heuristic: chosen from the aspect ratio of the region being split.
    /// </summary>
    /// <remarks>
    /// Design D2 defines <see cref="SplitAxis.Horizontal"/> as children arranged left-to-right
    /// (side by side) and <see cref="SplitAxis.Vertical"/> as children stacked top-to-bottom.
    /// A region wider than it is tall should place new windows side by side, so it resolves to
    /// <see cref="SplitAxis.Horizontal"/>; a region as tall as or taller than it is wide should
    /// stack, resolving to <see cref="SplitAxis.Vertical"/>.
    /// <para>
    /// Spec/design note: LE-4's literal wording ("width &gt; height -&gt; Vertical split
    /// (side-by-side)") uses the opposite enum label from what this method returns. That literal
    /// wording matches cosmic-comp's original <c>Orientation</c> convention (verified against
    /// <c>cosmic-epoch/cosmic-comp/src/shell/layout/tiling/mod.rs</c>: <c>Orientation::Vertical</c>
    /// there measures <c>geo.size.w</c>, i.e. is the side-by-side axis) — precisely the
    /// "inverted" naming design D2 says CosmicWin rejects. This method implements D2's own
    /// definition of what the enum values mean, applied to LE-4's behavioral intent (wide
    /// regions split side by side, tall regions stack). Flagged for spec reconciliation; see
    /// sdd/cosmic-win/apply-progress.
    /// </para>
    /// </remarks>
    public static SplitAxis ChooseSplitAxis(int width, int height) =>
        width > height ? SplitAxis.Horizontal : SplitAxis.Vertical;

    /// <summary>
    /// D3 <c>AddChild</c>, ported from cosmic-comp's <c>Data::add_window</c>: inserts
    /// <paramref name="child"/> into <paramref name="group"/> at <paramref name="index"/>,
    /// giving it an equal share of the group's length and proportionally shrinking existing
    /// siblings so that <c>Sizes.Sum() == GroupLength</c> always holds afterward (design D1).
    /// The new child absorbs any rounding remainder, guaranteeing the invariant exactly.
    /// </summary>
    public static void AddChild(GroupNode group, Node child, int index)
    {
        if (index < 0 || index > group.Children.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        int groupLength = group.GroupLength;
        int equalShare = groupLength / (group.Sizes.Count + 1);
        int remainder = groupLength - equalShare;

        for (int i = 0; i < group.Sizes.Count; i++)
        {
            group.Sizes[i] = groupLength == 0
                ? 0
                : (int)Math.Round(group.Sizes[i] / (double)groupLength * remainder);
        }

        int usedSize = group.Sizes.Sum();
        int newSize = groupLength - usedSize;

        child.Parent = group;
        group.Children.Insert(index, child);
        group.Sizes.Insert(index, newSize);
    }

    /// <summary>
    /// Splits <paramref name="leaf"/> into a new <see cref="GroupNode"/> containing the
    /// original leaf and a new leaf for <paramref name="newWindow"/>, choosing the group's axis
    /// via <see cref="ChooseSplitAxis"/> (LE-4). Callers are responsible for replacing
    /// <paramref name="leaf"/> with the returned group in its former position — parent
    /// back-references are out of scope for this work unit (see WU5).
    /// </summary>
    public static GroupNode AddChild(LeafNode leaf, WindowRef newWindow, int regionWidth, int regionHeight)
    {
        var axis = ChooseSplitAxis(regionWidth, regionHeight);
        int groupLength = axis == SplitAxis.Horizontal ? regionWidth : regionHeight;

        var group = new GroupNode(axis) { GroupLength = groupLength };
        leaf.Parent = group;
        group.Children.Add(leaf);
        group.Sizes.Add(groupLength);

        AddChild(group, new LeafNode(newWindow), index: 1);

        return group;
    }

    /// <summary>
    /// D3 <c>RemoveChild</c>: removes the child (and its size) at <paramref name="index"/> from
    /// <paramref name="group"/>, proportionally redistributing its freed size among the remaining
    /// siblings so that <c>Sizes.Sum() == GroupLength</c> continues to hold (design D1). Unlike
    /// <see cref="AddChild(GroupNode,Node,int)"/> — whose rounding remainder lands on the newly
    /// inserted child — removal has no "new" element to absorb overflow into, so the convention
    /// here is that the LAST remaining sibling absorbs the rounding remainder.
    /// </summary>
    /// <returns>The removed node.</returns>
    public static Node RemoveChild(GroupNode group, int index)
    {
        if (index < 0 || index >= group.Children.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var removed = group.Children[index];
        int removedSize = group.Sizes[index];

        group.Children.RemoveAt(index);
        group.Sizes.RemoveAt(index);
        removed.Parent = null;

        if (group.Sizes.Count > 0)
        {
            int remainingLength = group.Sizes.Sum();
            int distributed = 0;

            for (int i = 0; i < group.Sizes.Count - 1; i++)
            {
                int extra = remainingLength == 0
                    ? removedSize / group.Sizes.Count
                    : (int)Math.Round(group.Sizes[i] / (double)remainingLength * removedSize);
                group.Sizes[i] += extra;
                distributed += extra;
            }

            group.Sizes[^1] += removedSize - distributed;
        }

        return removed;
    }

    /// <summary>
    /// LE-2 step 1-3, extracted for reuse: walks up from <paramref name="from"/> via <see
    /// cref="Node.Parent"/>, looking for the nearest ancestor <see cref="GroupNode"/> whose <see
    /// cref="GroupNode.Axis"/> matches <paramref name="direction"/>'s axis AND has a child at the
    /// boundary <paramref name="direction"/> implies (index ± 1) from the child subtree
    /// containing <paramref name="from"/> at that ancestor level. Returns <see
    /// langword="null"/> if the walk reaches the tree root (a node with no <see
    /// cref="Node.Parent"/>) without finding a match.
    /// </summary>
    /// <remarks>
    /// Design D3/LE-6: this exact helper is reused, unmodified, by WU6's <c>ResizeNode</c> — the
    /// returned <see cref="AncestorMatch.ChildIndex"/> identifies the "target" subtree for both
    /// callers; <c>NextFocus</c> reads the sibling at <c>ChildIndex + step</c>, while
    /// <c>ResizeNode</c> transfers a size ratio between <c>ChildIndex</c> and that same neighbor.
    /// </remarks>
    public static AncestorMatch? FindMatchingAncestor(Direction direction, Node from)
    {
        var axis = AxisOf(direction);
        int step = StepOf(direction);

        Node current = from;
        while (current.Parent is GroupNode parent)
        {
            int index = parent.Children.IndexOf(current);
            int candidate = index + step;

            if (parent.Axis == axis && candidate >= 0 && candidate < parent.Children.Count)
            {
                return new AncestorMatch(parent, index);
            }

            current = parent;
        }

        return null;
    }

    /// <summary>
    /// LE-2 "Directional focus — tree walk": moves focus from <paramref name="focused"/> in
    /// <paramref name="direction"/> by delegating the ancestor search to <see
    /// cref="FindMatchingAncestor"/>, then descending depth-first into the sibling at the
    /// matching boundary to find its first leaf. Returns <see cref="FocusResult.NoMatch"/> (LE-2
    /// step 4) rather than performing any geometric/nearest-window search when no match exists.
    /// </summary>
    public static FocusResult NextFocus(Direction direction, LeafNode focused)
    {
        var match = FindMatchingAncestor(direction, focused);
        if (match is null)
        {
            return FocusResult.NoMatch;
        }

        int step = StepOf(direction);
        var sibling = match.Value.Ancestor.Children[match.Value.ChildIndex + step];
        return FocusResult.Found(FirstLeaf(sibling));
    }

    private static LeafNode FirstLeaf(Node node) => node switch
    {
        LeafNode leaf => leaf,
        GroupNode { Children.Count: > 0 } group => FirstLeaf(group.Children[0]),
        GroupNode => throw new InvalidOperationException("Cannot descend into an empty group."),
        _ => throw new InvalidOperationException($"Unknown node type: {node.GetType()}")
    };

    private static SplitAxis AxisOf(Direction direction) => direction switch
    {
        Direction.Left or Direction.Right => SplitAxis.Horizontal,
        Direction.Up or Direction.Down => SplitAxis.Vertical,
        _ => throw new ArgumentOutOfRangeException(nameof(direction))
    };

    private static int StepOf(Direction direction) => direction switch
    {
        Direction.Right or Direction.Down => 1,
        Direction.Left or Direction.Up => -1,
        _ => throw new ArgumentOutOfRangeException(nameof(direction))
    };

    /// <summary>
    /// LE-3 "Orientation toggle": flips <paramref name="focused"/>'s immediate parent group's
    /// <see cref="GroupNode.Axis"/> in place (Horizontal&#8596;Vertical). <see
    /// cref="GroupNode.Children"/> order and <see cref="GroupNode.Sizes"/> ratios are left
    /// untouched — the group's existing children and their proportions are unaffected by the
    /// toggle, only the axis label they are interpreted against changes. Does not pre-select
    /// orientation for any future split (LE-4's <see cref="ChooseSplitAxis"/> is unrelated).
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if <paramref name="focused"/> had an immediate parent group whose
    /// axis was flipped; <see langword="false"/> (no-op) if <paramref name="focused"/> is a tree
    /// root with no parent to flip.
    /// </returns>
    public static bool ToggleAxis(Node focused)
    {
        if (focused.Parent is not GroupNode parent)
        {
            return false;
        }

        parent.Axis = parent.Axis == SplitAxis.Horizontal ? SplitAxis.Vertical : SplitAxis.Horizontal;
        return true;
    }
}
