namespace CosmicWin.Layout;

/// <summary>
/// The axis along which a <see cref="GroupNode"/> arranges its children (LE-1).
/// </summary>
/// <remarks>
/// <see cref="Horizontal"/> means children are arranged left-to-right (side by side,
/// <see cref="GroupNode.Sizes"/> are widths); <see cref="Vertical"/> means children are arranged
/// top-to-bottom (stacked, <see cref="GroupNode.Sizes"/> are heights). This is a deliberate
/// naming choice: the reference implementation's own <c>Orientation::Horizontal</c> actually measures
/// <c>size.h</c> (i.e. it means "stacked", the opposite of what the name suggests) — a trap this
/// design explicitly avoids by making the enum name match the visual arrangement.
/// </remarks>
public enum SplitAxis
{
    Horizontal,
    Vertical
}
