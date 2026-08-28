namespace CosmicWin.Layout;

/// <summary>
/// Base type for the tiling layout tree (LE-1): every node is either a <see cref="LeafNode"/>
/// (a tracked window) or a <see cref="GroupNode"/> (a split containing other nodes).
/// </summary>
/// <remarks>
/// Geometry (e.g. last-arranged bounds) is intentionally not modeled here yet — <c>Arrange()</c>
/// is out of scope for this work unit, which covers only the node model and <c>AddChild</c>
/// (see Tasks 1.15-1.16).
/// </remarks>
public abstract record Node
{
    /// <summary>The bounds assigned by the most recent arrangement pass.</summary>
    public Rect LastGeometry { get; set; }

    /// <summary>
    /// The immediate containing <see cref="GroupNode"/>, or <see langword="null"/> for a tree
    /// root. Introduced in (LE-2) to support <c>NextFocus</c>'s ancestor tree-walk and its
    /// shared <c>FindMatchingAncestor</c> helper (also reused by 's <c>ResizeNode</c>, per
    /// the design/LE-6). Wired by <see cref="LayoutTree.AddChild(GroupNode,Node,int)"/> on
    /// insertion and cleared by <see cref="LayoutTree.RemoveChild"/> on removal.
    /// </summary>
    public GroupNode? Parent { get; set; }
}
