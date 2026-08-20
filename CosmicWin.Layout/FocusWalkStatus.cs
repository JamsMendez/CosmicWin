namespace CosmicWin.Layout;

/// <summary>
/// The outcome of a <see cref="LayoutTree.NextFocus"/> ancestor tree-walk (LE-2).
/// </summary>
/// <remarks>
/// Deliberately explicit rather than a null-meaning-error <c>LeafNode?</c> return: MM-5 (a later
/// work unit) needs to distinguish "the walk reached the tree root with no match" (fall through
/// to monitor switching) from other outcomes without relying on null as an implicit signal.
/// </remarks>
public enum FocusWalkStatus
{
    /// <summary>A matching-orientation ancestor with an available sibling boundary was found.</summary>
    Found,

    /// <summary>The walk reached the tree root without finding a match (LE-2 step 4, MM-5).</summary>
    NoMatch
}
