namespace CosmicWin.Layout;

/// <summary>
/// A leaf of the layout tree: a single tracked window (LE-1).
/// </summary>
public sealed record LeafNode(WindowRef Window) : Node;
