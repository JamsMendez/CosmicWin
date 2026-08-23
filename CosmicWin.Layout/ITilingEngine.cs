namespace CosmicWin.Layout;

/// <summary>
/// The Win32-free behavior exposed by the Phase 1 tiling engine.
/// </summary>
/// <remarks>
/// The design sketch's window-based Insert/Remove operations are intentionally deferred until
/// the App layer defines root ownership and window lookup semantics. This contract exposes only
/// operations the current tree model can implement without inventing Phase 2 behavior.
/// </remarks>
public interface ITilingEngine
{
    FocusResult NextFocus(Direction direction, LeafNode focused);

    bool MoveNode(Direction direction, Node focused);

    bool ToggleAxis(Node focused);

    bool ResizeNode(Direction direction, Node focused, double step = LayoutTree.DefaultResizeStep);

    IReadOnlyList<(WindowRef Window, Rect Bounds)> Arrange(Rect workArea);

    /// <summary>
    /// Removes <paramref name="focused"/> from wherever it currently sits in the tree (inside a
    /// group, or as the bare root). Exposed on the engine -- not tied to any concrete tree type --
    /// so the shared arrange choke point can evict an untileable leaf by construction without depending on a specific implementation.
    /// </summary>
    bool Remove(Node focused);
}
