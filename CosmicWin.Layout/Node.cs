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
public abstract record Node
{
    /// <summary>
    /// The immediate containing <see cref="GroupNode"/>, or <see langword="null"/> for a tree
    /// root. Introduced in WU5 (LE-2) to support <c>NextFocus</c>'s ancestor tree-walk and its
    /// shared <c>FindMatchingAncestor</c> helper (also reused by WU6's <c>ResizeNode</c>, per
    /// design D3/LE-6). Wired by <see cref="LayoutTree.AddChild(GroupNode,Node,int)"/> on
    /// insertion and cleared by <see cref="LayoutTree.RemoveChild"/> on removal.
    /// </summary>
    public GroupNode? Parent { get; set; }
}
