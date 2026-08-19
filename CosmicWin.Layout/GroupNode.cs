namespace CosmicWin.Layout;

/// <summary>
/// A split in the layout tree: an n-ary group of children arranged along <see cref="Axis"/>
/// (LE-1).
/// </summary>
/// <remarks>
/// Design D1: <see cref="Sizes"/> holds absolute values (not ratios) along the group's stack
/// axis, one per entry in <see cref="Children"/>, and <c>Sizes.Sum() == GroupLength</c> MUST
/// hold after every tree mutation. <see cref="GroupLength"/> is the group's total length along
/// <see cref="Axis"/> — a scalar rather than a full rectangle because full arrangement geometry
/// (<c>Arrange()</c>) is out of scope for this work unit (see WU6, tasks 1.15-1.16).
/// </remarks>
public sealed record GroupNode(SplitAxis Axis) : Node
{
    public List<Node> Children { get; } = [];

    public List<int> Sizes { get; } = [];

    public int GroupLength { get; set; }
}
