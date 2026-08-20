namespace CosmicWin.Layout;

/// <summary>
/// The result of <see cref="LayoutTree.NextFocus"/> (LE-2): an explicit <see cref="Status"/>
/// alongside the resolved <see cref="Leaf"/> when found.
/// </summary>
public readonly record struct FocusResult(FocusWalkStatus Status, LeafNode? Leaf)
{
    /// <summary>The shared "no match" result — the walk reached the tree root (LE-2 step 4).</summary>
    public static FocusResult NoMatch { get; } = new(FocusWalkStatus.NoMatch, null);

    /// <summary>Builds a "found" result wrapping the resolved leaf.</summary>
    public static FocusResult Found(LeafNode leaf) => new(FocusWalkStatus.Found, leaf);
}
