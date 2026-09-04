namespace CosmicWin.Layout;

/// <summary>
/// Tree operations for the tiling layout engine. This work unit covers only node
/// construction and <see cref="AddChild(GroupNode,Node,int)"/> — see / for
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

    bool ITilingEngine.MoveNode(Direction direction, Node focused)
    {
        // The move can leave the group the node came FROM redundant (one child) or empty. Prune
        // needs the tree to re-seat a collapsing ROOT, which the static core deliberately cannot
        // reach -- so the engine-facing entry point heals that last level.
        var origin = focused.Parent as GroupNode;
        if (!MoveNode(direction, focused))
        {
            return false;
        }

        Prune(this, origin);
        return true;
    }

    bool ITilingEngine.ToggleAxis(Node focused) => ToggleAxis(focused);

    bool ITilingEngine.ResizeNode(
        Direction direction, Node focused, double step, int minLength, int maxLength) =>
        ResizeNode(direction, focused, step, DefaultMinRatio, minLength, maxLength);

    bool ITilingEngine.Remove(Node focused) => Remove(focused);

    /// <summary>
    /// Removes <paramref name="focused"/> from wherever it currently sits: inside a group
    /// (redistributing the freed size via <see cref="RemoveChild"/>), or as the bare <see
    /// cref="Root"/> (cleared to <see langword="null"/>). Mirrors the App layer's equivalent
    /// registry-driven node removal, exposed here so the App-layer arrange choke point can evict an
    /// untileable leaf through the <see cref="ITilingEngine"/> abstraction alone (
    /// CRITICAL) without this Win32-free assembly depending on App-layer types.
    /// </summary>
    public bool Remove(Node focused)
    {
        if (focused.Parent is GroupNode parent)
        {
            var index = parent.Children.IndexOf(focused);
            if (index < 0)
            {
                return false;
            }

            RemoveChild(parent, index);
            Prune(this, parent);
            return true;
        }

        if (ReferenceEquals(Root, focused))
        {
            Root = null;
            return true;
        }

        return false;
    }

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
    /// wording follows the inverted convention in which the label <c>Vertical</c>
    /// names the side-by-side axis — precisely the
    /// "inverted" naming the design says CosmicWin rejects. This method implements D2's own
    /// definition of what the enum values mean, applied to LE-4's behavioral intent (wide
    /// regions split side by side, tall regions stack). Flagged for spec reconciliation; see
    /// the design notes.
    /// </para>
    /// </remarks>
    public static SplitAxis ChooseSplitAxis(int width, int height) =>
        width > height ? SplitAxis.Horizontal : SplitAxis.Vertical;

    /// <summary>
    /// D3 <c>AddChild</c>: inserts
    /// <paramref name="child"/> into <paramref name="group"/> at <paramref name="index"/>,
    /// giving it an equal share of the group's length and proportionally shrinking existing
    /// siblings so that <c>Sizes.Sum == GroupLength</c> always holds afterward.
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
    /// <c>TreeManager</c>'s MM-3 reparent -- can move it without orphaning an existing
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
    /// Heals the tree above a group a node was just removed from, so closing a window gives its
    /// space back instead of stranding it. Walks upward from <paramref name="from"/>:
    /// <list type="bullet">
    /// <item>a group left with NO children is detached from its parent (or clears the root), and the
    /// walk continues, since that detachment may hollow out the level above it too;</item>
    /// <item>a group left with ONE child collapses into that child, which inherits the group's exact
    /// slot and size;</item>
    /// <item>a group still holding two or more children is doing its job, and the walk stops.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// The empty case is the visible defect (measured): <see cref="Arrange"/> answers an
    /// empty group by zeroing its length and returning, but the PARENT still reserves a slot and a
    /// size for it, so that region is claimed and nothing is ever drawn there. A flat tree never
    /// showed this — there was only the root group, whose own <see cref="RemoveChild"/>
    /// redistributes — so it surfaced the moment new windows started splitting the focused tile.
    /// The single-child case is the quiet half: it draws correctly but leaves a level that no longer
    /// means anything, and both LE-2's tree walk and LE-5's moves count levels.
    /// </remarks>
    public static void Prune(LayoutTree tree, GroupNode? from)
    {
        for (var group = from; group is not null;)
        {
            var parent = group.Parent as GroupNode;
            int slot = parent?.Children.IndexOf(group) ?? -1;

            if (group.Children.Count == 0)
            {
                if (parent is not null && slot >= 0)
                {
                    RemoveChild(parent, slot);
                }
                else if (ReferenceEquals(tree.Root, group))
                {
                    tree.Root = null;
                }

                group = parent;
                continue;
            }

            if (group.Children.Count == 1)
            {
                var survivor = group.Children[0];
                if (parent is not null && slot >= 0)
                {
                    // The survivor takes the collapsed group's slot outright: Sizes is untouched,
                    // so no sibling moves because of a close.
                    survivor.Parent = parent;
                    parent.Children[slot] = survivor;
                }
                else if (ReferenceEquals(tree.Root, group))
                {
                    survivor.Parent = null;
                    tree.Root = survivor;
                }
            }

            return;
        }
    }

    /// <summary>
    /// LE-4 as its own scenario states it: a new window SPLITS the tile it lands on, so the group
    /// replaces <paramref name="leaf"/> exactly where the leaf sat -- same slot, same slot size,
    /// same siblings. This is what makes nesting happen at all during ordinary use; appending to the
    /// root group instead produces a flat row forever, which is not the intended behaviour.
    /// </summary>
    /// <remarks>
    /// <see cref="AddChild(LeafNode, WindowRef, int, int)"/> alone is NOT enough for a nested leaf:
    /// it re-parents the leaf into the new group and returns, leaving the old parent still listing
    /// the leaf. The leaf then has two parents and the group is unreachable. A root leaf is
    /// unaffected only because its caller assigns <c>Root</c> by hand.
    /// </remarks>
    public static GroupNode SplitLeafInPlace(
        LeafNode leaf, WindowRef newWindow, int regionWidth, int regionHeight)
    {
        // Captured BEFORE the split -- AddChild re-parents the leaf into the new group.
        var parent = leaf.Parent as GroupNode;
        int slot = parent?.Children.IndexOf(leaf) ?? -1;

        var group = AddChild(leaf, newWindow, regionWidth, regionHeight);
        if (parent is null || slot < 0)
        {
            return group;
        }

        // The group inherits the leaf's slot outright: Sizes is untouched, so no sibling moves.
        group.Parent = parent;
        parent.Children[slot] = group;
        return group;
    }

    /// <summary>
    /// D3 <c>RemoveChild</c>: removes the child (and its size) at <paramref name="index"/> from
    /// <paramref name="group"/>, proportionally redistributing its freed size among the remaining
    /// siblings so that <c>Sizes.Sum == GroupLength</c> continues to hold. Unlike
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
    /// This exact helper is reused, unmodified, by 's <c>ResizeNode</c> — the
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
    /// cref="FindMatchingAncestor"/>, then descending into the sibling at the matching boundary to
    /// find the leaf that actually TOUCHES that boundary (<see cref="NearestLeaf"/>). Returns
    /// <see cref="FocusResult.NoMatch"/> (LE-2 step 4) rather than performing any
    /// geometric/nearest-window search when no match exists.
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
        return FocusResult.Found(NearestLeaf(sibling, direction));
    }

    /// <summary>
    /// The leaf inside <paramref name="node"/> that shares the boundary just crossed travelling in
    /// <paramref name="direction"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Public because the boundary being crossed is not always a boundary INSIDE this tree:
    /// <c>TreeManager</c>'s monitor fall-through crosses between two trees entirely and lands in
    /// one of them, which is the same question asked one level up. It kept a private first-child
    /// copy of this descent and carried the identical defect for the identical reason. The rule
    /// belongs to the tree, so both askers now read it from here.
    /// </para>
    /// <para>
    /// This REPLACES an unconditional "descend through <c>Children[0]</c>", which was reported from
    /// real use as focus skipping a window: with A C B on screen, Alt+H from B landed on A. B's
    /// left-hand sibling is the group holding A and C, and its leading child is the one FURTHEST
    /// from the boundary being crossed — C is the window the user is pointing at.
    /// </para>
    /// <para>
    /// The old rule looked right because the only test that exercised it pressed Right, and for
    /// Right the leading child IS the nearest one. Direction was never part of the answer, so half
    /// the axis was wrong and unmeasured.
    /// </para>
    /// <para>
    /// The reversal applies ONLY where it means something: a group stacking along the direction
    /// travelled has a near end and a far end. A PERPENDICULAR group does not — every child of
    /// it touches the boundary equally — so the leading child stays the answer there rather
    /// than being flipped for a symmetry this tree cannot see. Choosing better among those would
    /// need focus history, which is not modelled here.
    /// </para>
    /// </remarks>
    public static LeafNode NearestLeaf(Node node, Direction direction) => node switch
    {
        LeafNode leaf => leaf,
        GroupNode { Children.Count: > 0 } group =>
            NearestLeaf(group.Children[EntryIndexOf(group, direction)], direction),
        GroupNode => throw new InvalidOperationException("Cannot descend into an empty group."),
        _ => throw new InvalidOperationException($"Unknown node type: {node.GetType()}")
    };

    /// <summary>
    /// Which child of <paramref name="group"/> the descent enters through: the trailing one when
    /// travelling Left/Up along the group's own axis, the leading one in every other case.
    /// </summary>
    private static int EntryIndexOf(GroupNode group, Direction direction) =>
        group.Axis == AxisOf(direction) && StepOf(direction) < 0
            ? group.Children.Count - 1
            : 0;

    private static SplitAxis AxisOf(Direction direction) => direction switch
    {
        Direction.Left or Direction.Right => SplitAxis.Horizontal,
        Direction.Up or Direction.Down => SplitAxis.Vertical,
        _ => throw new ArgumentOutOfRangeException(nameof(direction))
    };

    private static Direction Opposite(Direction direction) => direction switch
    {
        Direction.Left => Direction.Right,
        Direction.Right => Direction.Left,
        Direction.Up => Direction.Down,
        Direction.Down => Direction.Up,
        _ => throw new ArgumentOutOfRangeException(nameof(direction))
    };

    private static int StepOf(Direction direction) => direction switch
    {
        Direction.Right or Direction.Down => 1,
        Direction.Left or Direction.Up => -1,
        _ => throw new ArgumentOutOfRangeException(nameof(direction))
    };

    /// <summary>
    /// LE-5, re-cut as an ancestor walk: it climbs the tree until it finds a level
    /// that can absorb the move, instead of giving up at the node's own parent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This REPLACES the original "act only inside <c>focused.Parent</c>" rule, which was
    /// reported from real use as the thing that makes CosmicWin feel stuck.
    /// Pushing a window at the edge of its group toward the outside used to return false and
    /// do nothing; HA-1's <c>Alt+[</c> existed so the user could hand-raise the scope that this walk
    /// now derives per keypress. <c>Alt+[</c> keeps its meaning -- deliberately moving a WHOLE group
    /// as one unit -- but is no longer required for an ordinary window to leave its group.
    /// </para>
    /// <para>
    /// Four cases, in this order:
    /// <list type="number">
    /// <item>axis mismatch at this level -- the level splits perpendicular and the focused node
    /// takes the leading or trailing half (<see cref="SplitOutOf"/>);</item>
    /// <item>axis matches and we ARRIVED here from below -- the node leaves its old group and joins
    /// this one beside the subtree it came out of;</item>
    /// <item>axis matches at our own level -- move INTO the neighbour when it is a group,
    /// otherwise swap with it, sizes included, which is
    /// the original LE-5 behaviour and the only branch that survives unchanged;</item>
    /// <item>no room at this level -- ascend and try again.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Deliberate choice: entering a perpendicular neighbouring group inserts at the edge the move
    /// came from -- a window pushed Right enters the neighbour at its start -- rather than in the
    /// MIDDLE of it. Same escape, but the
    /// landing slot follows the direction the user pressed instead of the group's parity.
    /// </para>
    /// </remarks>
    public static bool MoveNode(Direction direction, Node focused)
    {
        if (focused.Parent is not GroupNode origin)
        {
            return false;
        }

        int originIndex = origin.Children.IndexOf(focused);
        if (originIndex < 0)
        {
            return false;
        }

        var axis = AxisOf(direction);
        int step = StepOf(direction);

        Node child = focused;
        GroupNode? level = origin;

        while (level is not null)
        {
            int index = level.Children.IndexOf(child);

            // (1) This level stacks the wrong way for the requested direction.
            if (level.Axis != axis)
            {
                if (level.Children.Count < 2)
                {
                    return false;
                }

                SplitOutOf(level, focused, axis, step, origin, originIndex);
                return true;
            }

            int neighbourIndex = index + step;
            if (neighbourIndex >= 0 && neighbourIndex < level.Children.Count)
            {
                // (2) We climbed to get here, so this level is a NEW home, not a reshuffle.
                if (!ReferenceEquals(child, focused))
                {
                    Detach(origin, originIndex);
                    AddChild(level, focused, step < 0 ? index : index + 1);
                    return true;
                }

                // (3a) The neighbour is a group of its own: move INTO it rather than treating it
                // as one opaque tile to swap with.
                if (level.Children.Count == 2 && level.Children[neighbourIndex] is GroupNode neighbour)
                {
                    Detach(origin, originIndex);
                    AddChild(neighbour, focused, step < 0 ? neighbour.Children.Count : 0);
                    return true;
                }

                // (3b) Exactly two of us here: swap, carrying sizes along. This is the original
                // LE-5 behaviour, and it is guarded on there being exactly two -- `len == 2`.
                if (level.Children.Count == 2)
                {
                    (level.Children[index], level.Children[neighbourIndex]) =
                        (level.Children[neighbourIndex], level.Children[index]);
                    (level.Sizes[index], level.Sizes[neighbourIndex]) =
                        (level.Sizes[neighbourIndex], level.Sizes[index]);
                    return true;
                }

                // (3c) Three or more siblings: make a new fork instead -- pair up
                // with the neighbour inside a new group of the SAME axis, taking the neighbour's
                // slot. Swapping instead looks equivalent on a flat row but is not: a swap is an
                // involution, so pressing the same direction again just undoes it and the window
                // can never travel past its neighbour. The fork is what makes the walk a reversible
                // CYCLE -- a window pushed
                // repeatedly keeps advancing instead of dead-ending after two presses.
                var neighbourNode = level.Children[neighbourIndex];
                Detach(origin, originIndex);

                int slot = level.Children.IndexOf(neighbourNode);
                if (slot < 0)
                {
                    return false;
                }

                var fork = new GroupNode(level.Axis)
                {
                    GroupLength = level.Sizes[slot],
                    Parent = level,
                };
                neighbourNode.Parent = fork;
                fork.Children.Add(neighbourNode);
                fork.Sizes.Add(fork.GroupLength);
                level.Children[slot] = fork;

                // Left/Up puts the mover AFTER the neighbour it just reached, Right/Down before it,
                // so the pair reads in the order the user travelled through them.
                AddChild(fork, focused, step < 0 ? 1 : 0);
                return true;
            }

            // (4) Out of room here -- carry on upward, now representing the level we just left.
            child = level;
            level = level.Parent as GroupNode;
        }

        return false;
    }

    /// <summary>
    /// Case (1) of <see cref="MoveNode"/>: <paramref name="level"/> splits along <paramref name="axis"/>, its
    /// former contents drop into a nested group, and <paramref name="focused"/> takes the half the
    /// direction points at.
    /// </summary>
    /// <remarks>
    /// <paramref name="level"/> is rewritten IN PLACE rather than replaced by a new group above it,
    /// for the same reason <see cref="ToggleAxis"/> flips an axis in place: other nodes' <see
    /// cref="Node.Parent"/> pointers and the parent's own <c>Children</c> slot reference this exact
    /// instance. It also means this works when <paramref name="level"/> is the tree ROOT, which a
    /// static helper cannot re-seat.
    /// </remarks>
    private static void SplitOutOf(
        GroupNode level, Node focused, SplitAxis axis, int step, GroupNode origin, int originIndex)
    {
        // Take the node out first, so its old siblings reclaim the space before anything is rebuilt.
        // The collapse is skipped when the origin IS this level: it is about to be refilled, and
        // collapsing a group mid-rewrite would detach it from the tree.
        if (ReferenceEquals(origin, level))
        {
            RemoveChild(origin, originIndex);
        }
        else
        {
            Detach(origin, originIndex);
        }

        // What stays behind. A wrapper around a SINGLE node is a level that means nothing, and both
        // LE-2's tree walk and LE-5's moves count levels -- see Prune's own remarks. Left in, these
        // accumulate one per move and strand the window: measured on the six-window spiral, where
        // pushing the last window produced H[D H[V[E] F]] and then dead-ended two presses later.
        Node retained;
        if (level.Children.Count == 1)
        {
            retained = level.Children[0];
        }
        else
        {
            var nested = new GroupNode(level.Axis) { GroupLength = level.GroupLength };
            foreach (var existing in level.Children)
            {
                existing.Parent = nested;
                nested.Children.Add(existing);
            }

            nested.Sizes.AddRange(level.Sizes);
            retained = nested;
        }

        level.Children.Clear();
        level.Sizes.Clear();
        level.Axis = axis;
        retained.Parent = level;
        level.Children.Add(retained);
        level.Sizes.Add(level.GroupLength);
        AddChild(level, focused, step < 0 ? 0 : 1);
    }

    /// <summary>
    /// Removes a node from the group it is leaving and collapses that group if the departure left
    /// it holding a single child. Stops short of the root case, which needs the tree -- the
    /// <see cref="ITilingEngine"/> entry point follows up with <see cref="Prune"/> for that.
    /// </summary>
    private static void Detach(GroupNode origin, int originIndex)
    {
        RemoveChild(origin, originIndex);

        if (origin.Children.Count != 1 || origin.Parent is not GroupNode above)
        {
            return;
        }

        int slot = above.Children.IndexOf(origin);
        if (slot < 0)
        {
            return;
        }

        var survivor = origin.Children[0];
        survivor.Parent = above;
        above.Children[slot] = survivor;
    }

    /// <summary>
    /// LE-6: grows the focused subtree by transferring space from its directional neighbor in
    /// the nearest matching-axis ancestor, without allowing that neighbor below the minimum.
    /// </summary>
    /// <param name="minLength">
    /// A length <paramref name="focused"/> may not be taken under, when it is the node that
    /// actually moves. Zero means no limit, which is every caller that has never measured a window.
    /// </param>
    /// <param name="maxLength">
    /// A length <paramref name="focused"/> may not be grown past, on the same terms.
    /// </param>
    public static bool ResizeNode(
        Direction direction,
        Node focused,
        double step = DefaultResizeStep,
        double minRatio = DefaultMinRatio,
        int minLength = 0,
        int maxLength = int.MaxValue)
    {
        // Grow into the neighbour on the pressed side when there is one...
        var match = FindMatchingAncestor(direction, focused);
        var grows = match is not null;

        // ...otherwise push the OPPOSITE boundary the same way, which shrinks the focused subtree.
        // Without this the leading child of a group could only ever get bigger, which is exactly
        // what was reported as "the decremental resize does not work": there was no shrink at all.
        var effective = grows ? direction : Opposite(direction);
        match ??= FindMatchingAncestor(effective, focused);
        if (match is null)
        {
            return false;
        }

        int requestedTransfer = (int)Math.Round(
            match.Value.Ancestor.GroupLength * step,
            MidpointRounding.AwayFromZero);

        return TransferAcross(
            effective, focused, grows ? requestedTransfer : -requestedTransfer, minRatio, minLength, maxLength);
    }

    /// <summary>
    /// Exchanges the SLOTS two nodes occupy. The shape of the tree is untouched: same groups, same
    /// axes, same sizes -- only which subtree sits in which slot changes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what dropping one tiled window onto another means. Sizes deliberately stay with the
    /// SLOT rather than travelling with the node: a swap is not a resize, and carrying the width
    /// along would drift the layout a little on every drop even though the user only reordered.
    /// </para>
    /// <para>
    /// Takes <see cref="Node"/> rather than <see cref="LeafNode"/> because the operation is about
    /// slots, not windows -- a whole split can be exchanged with a single tile and the result is
    /// still a well-formed tree. A node with no parent IS the whole tree, so it has no slot to
    /// exchange; that is refused rather than left to fail on a null.
    /// </para>
    /// </remarks>
    public static bool SwapLeaves(Node first, Node second)
    {
        if (ReferenceEquals(first, second)
            || first.Parent is not GroupNode firstParent
            || second.Parent is not GroupNode secondParent)
        {
            return false;
        }

        int firstIndex = firstParent.Children.IndexOf(first);
        int secondIndex = secondParent.Children.IndexOf(second);
        if (firstIndex < 0 || secondIndex < 0)
        {
            return false;
        }

        firstParent.Children[firstIndex] = second;
        secondParent.Children[secondIndex] = first;
        first.Parent = secondParent;
        second.Parent = firstParent;
        return true;
    }

    /// <summary>
    /// Translates one finished mouse edge-drag into the tree: every boundary the user actually
    /// moved transfers exactly the pixels it was dragged, so the reflow that follows lands the new
    /// proportions instead of undoing them.
    /// </summary>
    /// <remarks>
    /// An edge with no matching-axis neighbour is dropped, not approximated. Two windows split
    /// 1/2 - 1/2 across a single horizontal group have no boundary above or below either of them,
    /// so a vertical drag there has nothing to transfer and the window returns to its tile; add a
    /// split above or below and the same drag starts moving that boundary. The work-area border is
    /// the same case -- there is no neighbour on the far side of it to take space from.
    /// </remarks>
    public static bool ApplyEdgeDrag(
        Node focused,
        Rect previous,
        Rect current,
        double minRatio = DefaultMinRatio)
    {
        var changed = false;

        // Per AXIS, not per edge. A gesture that only MOVES the window reports both of an axis's
        // edges travelling the same distance, and reading those as two independent boundary drags
        // would distort the group while the user asked for no size change at all. A drag that does
        // change the length moves exactly one of the two edges, so the untouched one contributes a
        // zero delta and costs nothing.
        if (current.Width != previous.Width)
        {
            changed |= TransferAxis(
                Direction.Left,
                Direction.Right,
                focused,
                previous.X - current.X,
                current.X + current.Width - (previous.X + previous.Width),
                minRatio);
        }

        if (current.Height != previous.Height)
        {
            changed |= TransferAxis(
                Direction.Up,
                Direction.Down,
                focused,
                previous.Y - current.Y,
                current.Y + current.Height - (previous.Y + previous.Height),
                minRatio);
        }

        return changed;
    }

    /// <summary>
    /// Applies one axis of a drag, but only when the gesture ANCHORED one of its two edges.
    /// </summary>
    /// <remarks>
    /// That is the shape of a resize: Windows' own resize handles move one side and leave the
    /// opposite one where it was, so exactly one of the two deltas is zero. A gesture that moves
    /// BOTH is not someone dragging a boundary -- it is Aero Snap, which moves and resizes the
    /// window in a single drop, and it reports the same MOVESIZEEND a real drag does.
    /// <para>
    /// Reported: dragging a tiled window's title bar to a screen edge squeezed its neighbour to
    /// the minimum. Two tiles of 960 on 1920 came back [1728, 192], because a snap's whole-display
    /// width read as one enormous boundary drag. Measured over twelve real drags before this,
    /// every one of them anchored an edge exactly -- the untouched delta was 0, never a few
    /// pixels off -- so the rule costs a genuine drag nothing.
    /// </para>
    /// </remarks>
    private static bool TransferAxis(
        Direction towardStart,
        Direction towardEnd,
        Node focused,
        int startGrowth,
        int endGrowth,
        double minRatio)
    {
        if (startGrowth != 0 && endGrowth != 0)
        {
            return false;
        }

        var changed = TransferAcross(towardStart, focused, startGrowth, minRatio);
        return TransferAcross(towardEnd, focused, endGrowth, minRatio) || changed;
    }

    /// <summary>
    /// Moves <paramref name="growth"/> pixels across the boundary <paramref name="direction"/>
    /// names -- into the focused subtree when positive, out of it when negative -- never taking
    /// whichever side gives space up below <paramref name="minRatio"/> of its group.
    /// </summary>
    /// <remarks>
    /// Shared by the keyboard step and the mouse drag on purpose: the floor is the one rule both
    /// have to obey identically, and two copies of it is exactly how a window ends up squeezed to
    /// nothing on one path and not the other.
    /// </remarks>
    private static bool TransferAcross(
        Direction direction, Node focused, int growth, double minRatio,
        int minLength = 0, int maxLength = int.MaxValue)
    {
        if (growth == 0)
        {
            return false;
        }

        var match = FindMatchingAncestor(direction, focused);
        if (match is null)
        {
            return false;
        }

        var ancestor = match.Value.Ancestor;
        int targetIndex = match.Value.ChildIndex;
        int neighborIndex = targetIndex + StepOf(direction);

        // Whoever gives space up is the one that must not fall below the floor.
        int donorIndex = growth > 0 ? neighborIndex : targetIndex;
        int receiverIndex = growth > 0 ? targetIndex : neighborIndex;
        int minimumSize = (int)Math.Ceiling(ancestor.GroupLength * minRatio);
        int transfer = Math.Min(Math.Abs(growth), ancestor.Sizes[donorIndex] - minimumSize);

        // The caller's limits describe a WINDOW, so they apply only when the window itself is what
        // moves. A resize walks up to the nearest matching-axis ancestor, and what moves there may
        // be a whole branch holding several windows -- one window's floor says nothing about how
        // short that branch may be, and honouring it would let a single constrained window pin a
        // group it merely happens to live in.
        if (ReferenceEquals(ancestor.Children[targetIndex], focused))
        {
            var room = growth > 0
                ? maxLength - ancestor.Sizes[targetIndex]
                : ancestor.Sizes[targetIndex] - minLength;

            transfer = Math.Min(transfer, room);
        }

        if (transfer <= 0)
        {
            return false;
        }

        ancestor.Sizes[receiverIndex] += transfer;
        ancestor.Sizes[donorIndex] -= transfer;
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
    /// Gives <paramref name="leaf"/> <paramref name="by"/> more of its group's length and takes it
    /// from the other children. Reports whether the tree changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The answer for a window that needs more than its share and has no branch to be given. Real
    /// trees are BINARY -- <c>SplitLeafInPlace</c> divides the focused tile in two -- so a
    /// constrained window usually has exactly ONE sibling, and standing it beside "the others" is
    /// not available. What is available is the share itself.
    /// </para>
    /// <para>
    /// Exactly the shortfall and no more, so the cost is bounded by what the window actually needs
    /// rather than by a policy this file would have to invent.
    /// </para>
    /// <para>
    /// Donors are protected by the same floor ratio <see cref="ResizeNode"/> already uses, and it
    /// takes as much as they can spare rather than refusing when they cannot spare it all --
    /// reporting the amount, so the caller knows what is still missing.
    /// </para>
    /// <para>
    /// Partial on purpose, and it did not start that way. All-or-nothing reads well at ONE level --
    /// most of the way to a size a window will not go under is the same as nowhere -- and it is
    /// wrong the moment the space has to be gathered from more than one place. Measured: a window
    /// needing 772 in a group of 684 is refused here, and its ancestor is then asked for a share
    /// scaled by a ratio that never improved, so that is refused too. Taking the 234 this group can
    /// spare is what makes the ancestor's 174 enough. Whether a partial answer is worth keeping is
    /// the CALLER's question, and the caller is the one that can put the tree back.
    /// </para>
    /// <para>
    /// Spread evenly across the donors, because nothing here knows which neighbour deserves to give
    /// more. The invariant is what matters: <c>Sizes.Sum() == GroupLength</c> still holds, with the
    /// last donor absorbing the rounding remainder.
    /// </para>
    /// </remarks>
    public static int GrowWithinGroup(Node leaf, int by, double minRatio = DefaultMinRatio)
    {
        if (by <= 0 || leaf.Parent is not GroupNode group)
        {
            return 0;
        }

        var index = group.Children.IndexOf(leaf);
        if (index < 0 || group.Children.Count < 2)
        {
            return 0;
        }

        var floor = (int)Math.Ceiling(group.GroupLength * minRatio);
        var sparable = 0;
        for (var other = 0; other < group.Sizes.Count; other++)
        {
            if (other != index)
            {
                sparable += Math.Max(0, group.Sizes[other] - floor);
            }
        }

        by = Math.Min(by, sparable);
        if (by <= 0)
        {
            return 0;
        }

        var donors = group.Children.Count - 1;
        var taken = 0;
        var remaining = donors;
        for (var other = 0; other < group.Sizes.Count; other++)
        {
            if (other == index)
            {
                continue;
            }

            // The last donor absorbs the remainder, so the group adds up exactly rather than
            // drifting by a pixel per rounding.
            var share = remaining == 1 ? by - taken : by / donors;
            share = Math.Min(share, Math.Max(0, group.Sizes[other] - floor));
            group.Sizes[other] -= share;
            taken += share;
            remaining--;
        }

        group.Sizes[index] += taken;
        return taken;
    }

    /// <summary>
    /// Takes <paramref name="leaf"/> out of its group and stands it BESIDE its former siblings, on
    /// the other axis. Reports whether the tree changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>[A, B, C]</c> stacked becomes <c>[[A, C] stacked, B] side by side</c>. The siblings keep
    /// the axis they were given and B gets the FULL extent of the one it was failing on -- the
    /// whole height rather than a third of it.
    /// </para>
    /// <para>
    /// The shape a window with a minimum size needs. Three windows stacked on a 1376-tall field get
    /// 453 each, and a window that will not go under 772 cannot be one of them; the only answers
    /// used to be ejecting it from the tree or squashing every neighbour to make room. A branch of
    /// its own costs neither.
    /// </para>
    /// <para>
    /// It buys one axis and SELLS half the other, so a caller must measure both before committing:
    /// a window extracted from a row of three gains the full height and drops from the whole width
    /// to half of it. Nothing here can tell whether that trade helped, because nothing here knows
    /// what the window needs.
    /// </para>
    /// <para>
    /// Pure surgery, and deliberately ignorant of minimum sizes. Those are measured from real
    /// windows by the layer that owns them; this project has never seen a window and should not
    /// learn to.
    /// </para>
    /// <para>
    /// A pair needs no special case. <see cref="Prune"/> already collapses a group down to one
    /// child, so extracting from two windows leaves the pair on the other axis -- the honest answer,
    /// since a window that cannot share an axis with ONE neighbour gains nothing from a branch.
    /// </para>
    /// </remarks>
    public bool ExtractToOppositeAxis(Node leaf)
    {
        if (leaf.Parent is not GroupNode group)
        {
            return false;
        }

        var index = group.Children.IndexOf(leaf);
        if (index < 0 || group.Children.Count < 2)
        {
            return false;
        }

        var grandparent = group.Parent;
        var slot = grandparent?.Children.IndexOf(group) ?? -1;

        RemoveChild(group, index);

        // GroupLength is left at zero on purpose: the next arrange rescales a group whose sizes sum
        // to nothing into equal shares, which is exactly the split a fresh branch should start from.
        var wrapper = new GroupNode(
            group.Axis == SplitAxis.Horizontal ? SplitAxis.Vertical : SplitAxis.Horizontal);

        if (grandparent is not null && slot >= 0)
        {
            wrapper.Parent = grandparent;
            grandparent.Children[slot] = wrapper;
        }
        else
        {
            wrapper.Parent = null;
            Root = wrapper;
        }

        AddChild(wrapper, group, 0);
        AddChild(wrapper, leaf, 1);

        // The group may now hold a single child, which is a wrapper nobody needs. Pruning it here
        // keeps the pair case honest instead of leaving a one-child group for a later removal to
        // discover.
        Prune(this, group);
        return true;
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
