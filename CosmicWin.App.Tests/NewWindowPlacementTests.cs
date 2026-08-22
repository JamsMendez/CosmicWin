using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App.Tests;

/// <summary>
/// Where a NEW window lands. Measured on real hardware 2026-08-22: every window after the second was
/// appended to the end of the root group, next to the last one, no matter which window had focus.
/// That is neither what LE-4 says nor how COSMIC behaves.
///
/// LE-4's own scenario is explicit — "a new Horizontal-oriented Group REPLACES the Leaf, with both
/// windows side by side" — and the maintainer described the same thing from the product side: a new
/// window opens BESIDE or BELOW the focused one, side chosen by the focused tile's aspect ratio.
/// Only the very first split ever did this, because it was the only case where the tree root
/// happened to be a bare leaf.
/// </summary>
/// <remarks>
/// The consequence is deliberate and worth stating: splitting the focused tile means each new
/// window takes half of THAT tile, not an equal share of the row. Three windows opened without
/// moving focus give 1/2, 1/4, 1/4 -- not thirds. That is the i3/COSMIC model, and it is what makes
/// nested groups (and therefore HA-1's Alt+[) arise during ordinary use at all.
/// </remarks>
public sealed class NewWindowPlacementTests
{
    private sealed record Setup(TreeManager Trees, WindowRegistry Registry, FakeWorkspace Workspace, IDisplay Primary);

    private static Setup OneDisplay()
    {
        var primary = new FakeDisplay(
            new IntPtr(1), Rectangle.FromSize(0, 0, 1920, 1080), Rectangle.FromSize(0, 0, 1920, 1080), 1.0, true);
        var registry = new WindowRegistry();
        return new Setup(new TreeManager([primary], primary, registry), registry, new FakeWorkspace(), primary);
    }

    private static MultiMonitorWorkspaceAdapter Adapter(Setup s, Func<LeafNode?> focused) =>
        new(s.Workspace, s.Trees, s.Registry, () => ExceptionList.Empty, () => false, focused);

    private static LeafNode? Leaf(Setup s, RecordingWindow window) =>
        s.Registry.TryGetLeaf(window.Handle, out var leaf) ? leaf : null;

    private static RecordingWindow Window(int handle) =>
        new(new IntPtr(handle), Rectangle.FromSize(0, 0, 400, 300));

    /// <summary>
    /// The exact case the maintainer reported. Focus stays on the FIRST window while a third opens;
    /// it must split that window, not land next to the second one. On a 1920x1080 work area the
    /// focused tile is 960 wide by 1080 tall -- taller than wide -- so LE-4 stacks the newcomer
    /// BELOW it, which is precisely "abajo de la del focus actual".
    /// </summary>
    [Fact]
    public void ThirdWindow_SplitsTheFocusedTile_NotTheEndOfTheRow()
    {
        var s = OneDisplay();
        RecordingWindow? first = null;
        using var adapter = Adapter(s, () => first is null ? null : Leaf(s, first));

        first = Window(10);
        var second = Window(20);
        var third = Window(30);
        s.Workspace.RaiseWindowAdded(first);
        s.Workspace.RaiseWindowAdded(second);
        s.Workspace.RaiseWindowAdded(third);

        Assert.Equal(Rectangle.FromSize(0, 0, 960, 540), first.LastSetPosition);
        Assert.Equal(Rectangle.FromSize(0, 540, 960, 540), third.LastSetPosition);
        Assert.Equal(Rectangle.FromSize(960, 0, 960, 1080), second.LastSetPosition);
    }

    /// <summary>
    /// The same three windows with focus following the newest one -- the ordinary case, since a new
    /// window takes focus. Each newcomer splits the tile it opened into, so the layout keeps
    /// bisecting rather than flattening into equal columns.
    /// </summary>
    [Fact]
    public void EachNewWindow_SplitsWhicheverTileIsFocused()
    {
        var s = OneDisplay();
        RecordingWindow? focused = null;
        using var adapter = Adapter(s, () => focused is null ? null : Leaf(s, focused));

        var first = Window(10);
        focused = first;
        s.Workspace.RaiseWindowAdded(first);

        var second = Window(20);
        s.Workspace.RaiseWindowAdded(second);
        focused = second;

        var third = Window(30);
        s.Workspace.RaiseWindowAdded(third);

        // second's tile was 960x1080 (tall), so third stacks below IT, leaving first untouched.
        Assert.Equal(Rectangle.FromSize(0, 0, 960, 1080), first.LastSetPosition);
        Assert.Equal(Rectangle.FromSize(960, 0, 960, 540), second.LastSetPosition);
        Assert.Equal(Rectangle.FromSize(960, 540, 960, 540), third.LastSetPosition);
    }

    /// <summary>
    /// Splitting the focused tile is what creates nesting during ordinary use -- the reason HA-1's
    /// <c>Alt+[</c> has anything to select. A flat append could never produce a group inside a group.
    /// </summary>
    [Fact]
    public void SplittingTheFocusedTile_ProducesANestedGroup()
    {
        var s = OneDisplay();
        RecordingWindow? first = null;
        using var adapter = Adapter(s, () => first is null ? null : Leaf(s, first));

        first = Window(10);
        s.Workspace.RaiseWindowAdded(first);
        s.Workspace.RaiseWindowAdded(Window(20));
        s.Workspace.RaiseWindowAdded(Window(30));

        s.Trees.TryGetTree(s.Primary, out var tree);
        var root = Assert.IsType<GroupNode>(tree!.Root);
        Assert.Equal(SplitAxis.Horizontal, root.Axis);
        var nested = Assert.IsType<GroupNode>(root.Children[0]);
        Assert.Equal(SplitAxis.Vertical, nested.Axis);
        Assert.IsType<LeafNode>(root.Children[1]);
    }

    /// <summary>
    /// No focused leaf resolves -- nothing tracked is in the foreground -- so placement falls back to
    /// the previous append-at-the-end behaviour rather than dropping the window.
    /// </summary>
    [Fact]
    public void WhenNoFocusedLeafResolves_FallsBackToAppendingAtTheEnd()
    {
        var s = OneDisplay();
        using var adapter = Adapter(s, () => null);

        var first = Window(10);
        var second = Window(20);
        var third = Window(30);
        s.Workspace.RaiseWindowAdded(first);
        s.Workspace.RaiseWindowAdded(second);
        s.Workspace.RaiseWindowAdded(third);

        Assert.Equal(Rectangle.FromSize(0, 0, 640, 1080), first.LastSetPosition);
        Assert.Equal(Rectangle.FromSize(640, 0, 640, 1080), second.LastSetPosition);
        Assert.Equal(Rectangle.FromSize(1280, 0, 640, 1080), third.LastSetPosition);
    }

    /// <summary>
    /// A focused leaf living in ANOTHER monitor's tree must never be split by a window arriving on
    /// this one. Out of active scope as a product feature (decision #86, single monitor), but the
    /// guard is free and keeps the shared static honest for whatever tree it is handed.
    /// </summary>
    [Fact]
    public void AFocusedLeafFromAnotherTree_IsIgnored()
    {
        var s = OneDisplay();
        var strayTree = new LayoutTree(new LeafNode(new WindowRef(new IntPtr(999))));
        using var adapter = Adapter(s, () => (LeafNode)strayTree.Root!);

        var first = Window(10);
        var second = Window(20);
        s.Workspace.RaiseWindowAdded(first);
        s.Workspace.RaiseWindowAdded(second);

        Assert.Equal(Rectangle.FromSize(0, 0, 960, 1080), first.LastSetPosition);
        Assert.Equal(Rectangle.FromSize(960, 0, 960, 1080), second.LastSetPosition);
    }
}
