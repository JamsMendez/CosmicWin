namespace CosmicWin.Layout;

/// <summary>
/// Tree operations for the tiling layout engine. This work unit (WU4) covers only node
/// construction and <see cref="AddChild(GroupNode,Node,int)"/> — see WU5/WU6 (design D3) for
/// <c>RemoveChild</c>, <c>NextFocus</c>, <c>ToggleAxis</c>, <c>MoveNode</c>, <c>ResizeNode</c>,
/// and <c>Arrange</c>.
/// </summary>
public sealed class LayoutTree : ITilingEngine
{
    public const double DefaultResizeStep = 0.05;

    public const double DefaultMinRatio = 0.10;

    public LayoutTree(Node? root = null)
    {
        Root = root;
    }

    public Node? Root { get; set; }

    FocusResult ITilingEngine.NextFocus(Direction direction, LeafNode focused) =>
        NextFocus(direction, focused);

    bool ITilingEngine.MoveNode(Direction direction, Node focused) => MoveNode(direction, focused);

    bool ITilingEngine.ToggleAxis(Node focused) => ToggleAxis(focused);

    bool ITilingEngine.ResizeNode(Direction direction, Node focused, double step) =>
        ResizeNode(direction, focused, step);

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
    /// Splits <paramref name="leaf"/> into a new <see cref="GroupNode"/> containing the original
    /// leaf and <paramref name="newNode"/>, choosing the group's axis via
    /// <see cref="ChooseSplitAxis"/> (LE-4). Takes an existing <see cref="Node"/> (rather than a
    /// <see cref="WindowRef"/>) so callers that already hold a registered node -- e.g.
    /// <c>TreeManager</c>'s MM-3 reparent (task 3.4) -- can move it without orphaning an existing
    /// <c>WindowRegistry</c> mapping.
    /// </summary>
    public static GroupNode AddChild(LeafNode leaf, Node newNode, int regionWidth, int regionHeight)
    {
        var axis = ChooseSplitAxis(regionWidth, regionHeight);
        int groupLength = axis == SplitAxis.Horizontal ? regionWidth : regionHeight;

        var group = new GroupNode(axis) { GroupLength = groupLength };
        leaf.Parent = group;
        group.Children.Add(leaf);
        group.Sizes.Add(groupLength);

        AddChild(group, newNode, index: 1);

        return group;
    }

    /// <summary>
    /// Convenience overload for the common "a brand-new window arrives" case: wraps
    /// <paramref name="newWindow"/> in a fresh <see cref="LeafNode"/> and delegates to
    /// <see cref="AddChild(LeafNode,Node,int,int)"/>.
    /// </summary>
    public static GroupNode AddChild(LeafNode leaf, WindowRef newWindow, int regionWidth, int regionHeight) =>
        AddChild(leaf, new LeafNode(newWindow), regionWidth, regionHeight);

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
    /// LE-5: swaps a node with its directional sibling in a matching-axis group, or nests it
    /// with an adjacent sibling under a new group when the immediate axis does not match.
    /// </summary>
    public static bool MoveNode(Direction direction, Node focused)
    {
        if (focused.Parent is not GroupNode parent)
        {
            return false;
        }

        int focusedIndex = parent.Children.IndexOf(focused);
        var requestedAxis = AxisOf(direction);
        if (parent.Axis == requestedAxis)
        {
            int siblingIndex = focusedIndex + StepOf(direction);
            if (siblingIndex < 0 || siblingIndex >= parent.Children.Count)
            {
                return false;
            }

            (parent.Children[focusedIndex], parent.Children[siblingIndex]) =
                (parent.Children[siblingIndex], parent.Children[focusedIndex]);
            (parent.Sizes[focusedIndex], parent.Sizes[siblingIndex]) =
                (parent.Sizes[siblingIndex], parent.Sizes[focusedIndex]);
            return true;
        }

        if (parent.Children.Count < 2)
        {
            return false;
        }

        int adjacentIndex = focusedIndex + 1 < parent.Children.Count
            ? focusedIndex + 1
            : focusedIndex - 1;
        int insertionIndex = Math.Min(focusedIndex, adjacentIndex);
        var first = parent.Children[insertionIndex];
        var second = parent.Children[insertionIndex + 1];
        int firstSize = parent.Sizes[insertionIndex];
        int secondSize = parent.Sizes[insertionIndex + 1];
        int combinedSize = firstSize + secondSize;

        var nested = new GroupNode(requestedAxis)
        {
            GroupLength = combinedSize,
            Parent = parent
        };
        nested.Children.Add(first);
        nested.Children.Add(second);
        nested.Sizes.Add(firstSize);
        nested.Sizes.Add(secondSize);
        first.Parent = nested;
        second.Parent = nested;

        parent.Children.RemoveRange(insertionIndex, 2);
        parent.Sizes.RemoveRange(insertionIndex, 2);
        parent.Children.Insert(insertionIndex, nested);
        parent.Sizes.Insert(insertionIndex, combinedSize);
        return true;
    }

    /// <summary>
    /// LE-6: grows the focused subtree by transferring space from its directional neighbor in
    /// the nearest matching-axis ancestor, without allowing that neighbor below the minimum.
    /// </summary>
    public static bool ResizeNode(
        Direction direction,
        Node focused,
        double step = DefaultResizeStep,
        double minRatio = DefaultMinRatio)
    {
        var match = FindMatchingAncestor(direction, focused);
        if (match is null)
        {
            return false;
        }

        var ancestor = match.Value.Ancestor;
        int targetIndex = match.Value.ChildIndex;
        int neighborIndex = targetIndex + StepOf(direction);
        int requestedTransfer = (int)Math.Round(
            ancestor.GroupLength * step,
            MidpointRounding.AwayFromZero);
        int minimumSize = (int)Math.Ceiling(ancestor.GroupLength * minRatio);
        int available = ancestor.Sizes[neighborIndex] - minimumSize;
        int transfer = Math.Min(requestedTransfer, available);
        if (transfer <= 0)
        {
            return false;
        }

        ancestor.Sizes[targetIndex] += transfer;
        ancestor.Sizes[neighborIndex] -= transfer;
        return true;
    }

    /// <summary>
    /// Produces deterministic leaf geometry without moving windows or calling platform APIs.
    /// </summary>
    public IReadOnlyList<(WindowRef Window, Rect Bounds)> Arrange(Rect workArea)
    {
        var result = new List<(WindowRef Window, Rect Bounds)>();
        if (Root is null)
        {
            return result;
        }

        ArrangeNode(Root, workArea, result);
        return result;
    }

    private static void ArrangeNode(
        Node node,
        Rect bounds,
        List<(WindowRef Window, Rect Bounds)> result)
    {
        node.LastGeometry = bounds;
        if (node is LeafNode leaf)
        {
            result.Add((leaf.Window, bounds));
            return;
        }

        var group = (GroupNode)node;
        if (group.Children.Count == 0)
        {
            group.GroupLength = 0;
            return;
        }

        int newLength = group.Axis == SplitAxis.Horizontal ? bounds.Width : bounds.Height;
        RescaleSizes(group, newLength);

        int offset = 0;
        for (int index = 0; index < group.Children.Count; index++)
        {
            int size = group.Sizes[index];
            var childBounds = group.Axis == SplitAxis.Horizontal
                ? new Rect(bounds.X + offset, bounds.Y, size, bounds.Height)
                : new Rect(bounds.X, bounds.Y + offset, bounds.Width, size);
            ArrangeNode(group.Children[index], childBounds, result);
            offset += size;
        }
    }

    private static void RescaleSizes(GroupNode group, int newLength)
    {
        int oldLength = group.Sizes.Sum();
        int allocated = 0;
        for (int index = 0; index < group.Sizes.Count - 1; index++)
        {
            int scaled = oldLength == 0
                ? (int)Math.Round(newLength / (double)group.Sizes.Count, MidpointRounding.AwayFromZero)
                : (int)Math.Round(
                    group.Sizes[index] / (double)oldLength * newLength,
                    MidpointRounding.AwayFromZero);
            scaled = Math.Clamp(scaled, 0, newLength - allocated);
            group.Sizes[index] = scaled;
            allocated += scaled;
        }

        group.Sizes[^1] = newLength - allocated;
        group.GroupLength = newLength;
    }

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
