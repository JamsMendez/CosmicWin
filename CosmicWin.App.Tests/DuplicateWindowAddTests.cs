using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App.Tests;

/// <summary>
/// Measured on real hardware: the shell announced ONE HWND as added more than once, and the tree
/// kept a leaf for every announcement.
/// </summary>
/// <remarks>
/// <para>
/// From a supervised Photoshop session: adds per handle were exactly one for every ordinary window,
/// three for <c>class=Button proc=Photoshop.exe</c> and two for the main <c>class=Photoshop</c>
/// window. Nothing else in the session duplicated.
/// </para>
/// <para>
/// The damage is not the duplicate leaf itself, it is that the leaf is INVISIBLE to the code that
/// cleans up. <c>WindowRegistry.Register</c> overwrites <c>_leaves[handle]</c>, so the registry
/// only ever knows the newest leaf, while <c>TreeArranger</c> walks the TREE and finds both. One
/// handle therefore receives two different rectangles per arrange, and each reposition raises the
/// bounds-changed event that triggers the next arrange: 17,467 reflows of one handle in 57 seconds
/// were recorded, ~306 a second, and the trace file grew to 2.5 MB. Closing the window then
/// detaches only the registry's leaf, and the other keeps its slot forever -- the "empty space"
/// the user reported, still held by a window that no longer exists.
/// </para>
/// <para>
/// These facts assert what the eye checks rather than the tree shape alone: a lone window owns the
/// whole work area, and a survivor reclaims all of it. Asserting only "the leaf was replaced" would
/// stay green through this defect, because a leaf WAS replaced -- in the registry, not in the tree.
/// </para>
/// </remarks>
public sealed class DuplicateWindowAddTests
{
    private sealed record Setup(TreeManager Trees, WindowRegistry Registry, FakeWorkspace Workspace, IDisplay Primary);

    private static readonly Rectangle WorkArea = Rectangle.FromSize(0, 0, 1920, 1080);

    private static Setup OneDisplay()
    {
        var primary = new FakeDisplay(new IntPtr(1), WorkArea, WorkArea, 1.0, true);
        var registry = new WindowRegistry();
        return new Setup(new TreeManager([primary], primary, registry), registry, new FakeWorkspace(), primary);
    }

    private static RecordingWindow Window(int handle) =>
        new(new IntPtr(handle), Rectangle.FromSize(0, 0, 400, 300));

    private static MultiMonitorWorkspaceAdapter Adapter(Setup s, Func<RecordingWindow?> focused) =>
        new(s.Workspace, s.Trees, s.Registry, () => ExceptionList.Empty, () => false,
            () => focused() is { } window && s.Registry.TryGetLeaf(window.Handle, out var leaf) ? leaf : null);

    private static int LeafCountFor(Setup s, nint handle)
    {
        s.Trees.TryGetTree(s.Primary, out var tree);
        return tree?.Root is null ? 0 : s.Trees.LeavesOn(s.Primary).Count(leaf => leaf.Window.Handle == handle);
    }

    /// <summary>
    /// The whole defect in one line. A second announcement of a handle the tree already holds is
    /// the SAME window, so it must still be one leaf.
    /// </summary>
    [Fact]
    public void AnnouncingTheSameHandleTwice_LeavesExactlyOneLeafInTheTree()
    {
        var s = OneDisplay();
        using var adapter = Adapter(s, () => null);

        var window = Window(10);
        s.Workspace.RaiseWindowAdded(window);
        s.Workspace.RaiseWindowAdded(window);

        Assert.Equal(1, LeafCountFor(s, window.Handle));
    }

    /// <summary>
    /// What the user actually sees. One window on the desktop owns the whole work area; a second
    /// announcement of that same window must not split the screen with itself.
    /// </summary>
    [Fact]
    public void AnnouncingTheSameHandleTwice_StillGivesThatWindowTheWholeWorkArea()
    {
        var s = OneDisplay();
        using var adapter = Adapter(s, () => null);

        var window = Window(10);
        s.Workspace.RaiseWindowAdded(window);
        s.Workspace.RaiseWindowAdded(window);

        Assert.Equal(WorkArea, window.LastSetPosition);
    }

    /// <summary>
    /// The empty space, reproduced. The duplicate leaf is not registered, so the close cannot
    /// detach it, and the survivor is left tiling half a screen against a window that is gone.
    /// </summary>
    [Fact]
    public void ClosingAWindowThatWasAnnouncedTwice_LetsTheSurvivorReclaimTheWholeArea()
    {
        var s = OneDisplay();
        RecordingWindow? focused = null;
        using var adapter = Adapter(s, () => focused);

        var first = Window(10);
        var second = Window(20);
        focused = first;
        s.Workspace.RaiseWindowAdded(first);
        focused = second;
        s.Workspace.RaiseWindowAdded(second);
        s.Workspace.RaiseWindowAdded(second);

        s.Workspace.RaiseWindowRemoved(second);

        Assert.Equal(WorkArea, first.LastSetPosition);
        Assert.Equal(0, LeafCountFor(s, second.Handle));
    }

    /// <summary>
    /// The reflow storm's precondition: one handle must never be reachable at two geometries. The
    /// arrange pass positions every leaf it walks, so two leaves are two rectangles for one window
    /// and the bounds-changed event feeds the next arrange forever.
    /// </summary>
    [Fact]
    public void AnnouncingTheSameHandleTwice_NeverGivesOneHandleTwoGeometries()
    {
        var s = OneDisplay();
        RecordingWindow? focused = null;
        using var adapter = Adapter(s, () => focused);

        var first = Window(10);
        var second = Window(20);
        focused = first;
        s.Workspace.RaiseWindowAdded(first);
        focused = second;
        s.Workspace.RaiseWindowAdded(second);
        s.Workspace.RaiseWindowAdded(second);

        var geometries = s.Trees.LeavesOn(s.Primary)
            .Where(leaf => leaf.Window.Handle == second.Handle)
            .Select(leaf => leaf.LastGeometry)
            .Distinct()
            .ToArray();

        Assert.Single(geometries);
    }

    /// <summary>
    /// The guard must not swallow a genuine re-open. A handle the tree no longer holds is a new
    /// window as far as this adapter is concerned, whatever it was a moment ago -- Windows reuses
    /// HWND values, and refusing one would lose the window outright.
    /// </summary>
    [Fact]
    public void AnnouncingAHandleAgainAfterItWasRemoved_AddsItBack()
    {
        var s = OneDisplay();
        using var adapter = Adapter(s, () => null);

        var window = Window(10);
        s.Workspace.RaiseWindowAdded(window);
        s.Workspace.RaiseWindowRemoved(window);
        s.Workspace.RaiseWindowAdded(window);

        Assert.Equal(1, LeafCountFor(s, window.Handle));
        Assert.Equal(WorkArea, window.LastSetPosition);
    }
}
