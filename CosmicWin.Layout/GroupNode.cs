namespace CosmicWin.Layout;

/// <summary>
/// A split in the layout tree: an n-ary group of children arranged along <see cref="Axis"/>
/// (LE-1).
/// </summary>
/// <remarks>
/// <see cref="Sizes"/> holds absolute values (not ratios) along the group's stack
/// axis, one per entry in <see cref="Children"/>, and <c>Sizes.Sum() == GroupLength</c> MUST
/// hold after every tree mutation. <see cref="GroupLength"/> is the group's total length along
/// <see cref="Axis"/> — a scalar rather than a full rectangle because full arrangement geometry
/// (<c>Arrange</c>) is out of scope for this work unit (see, tasks 1.15-1.16).
/// </remarks>
public sealed record GroupNode(SplitAxis Axis) : Node
{
    /// <summary>
    /// The split axis, mutable (not <c>init</c>-only, overriding the record's default positional
    /// property) so that <see cref="LayoutTree.ToggleAxis"/> (LE-3) can flip it in place rather
    /// than replacing the group with a new instance — the group is referenced by other nodes'
    /// <see cref="Node.Parent"/> pointers and by its own parent's <see cref="Children"/> list, so
    /// an in-place flip is required to keep those references valid.
    /// </summary>
    public SplitAxis Axis { get; set; } = Axis;

    public List<Node> Children { get; } = [];

    public List<int> Sizes { get; } = [];

    public int GroupLength { get; set; }
}
