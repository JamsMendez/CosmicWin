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
        group.Children.Add(leaf);
        group.Sizes.Add(groupLength);

        AddChild(group, new LeafNode(newWindow), index: 1);

        return group;
    }
}
