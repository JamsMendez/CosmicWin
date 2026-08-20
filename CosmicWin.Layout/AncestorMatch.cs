namespace CosmicWin.Layout;

/// <summary>
/// The result of <see cref="LayoutTree.FindMatchingAncestor"/>: the nearest ancestor
/// <see cref="GroupNode"/> whose orientation matches the requested <see cref="Direction"/>'s
/// axis, and the index within that ancestor's <see cref="GroupNode.Children"/> that contains
/// (or is) the node the walk started from.
/// </summary>
/// <remarks>
/// Design D3/LE-6: this is the exact shape WU6's <c>ResizeNode</c> is meant to reuse — <see
/// cref="ChildIndex"/> identifies the "target" subtree, and its neighbor at <c>ChildIndex +
/// step</c> (the step implied by the original <see cref="Direction"/>) is the resize/focus
/// boundary on the other side.
/// </remarks>
public readonly record struct AncestorMatch(GroupNode Ancestor, int ChildIndex);
