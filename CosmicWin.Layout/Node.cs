namespace CosmicWin.Layout;

/// <summary>
/// Base type for the tiling layout tree (LE-1): every node is either a <see cref="LeafNode"/>
/// (a tracked window) or a <see cref="GroupNode"/> (a split containing other nodes).
/// </summary>
/// <remarks>
/// Geometry (e.g. last-arranged bounds) is intentionally not modeled here yet — <c>Arrange()</c>
/// is out of scope for this work unit (WU4 covers only the node model and <c>AddChild</c>; see
/// WU6 tasks 1.15-1.16).
/// </remarks>
public abstract record Node;
