using System.Linq;
using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;
using CosmicWin.Layout;

namespace CosmicWin.App.Tests;

/// <summary>
/// Phase 3 tasks 3.1-3.8 (design D4, spec MM-1..MM-5): <see cref="TreeManager"/> owns one
/// <see cref="LayoutTree"/> per connected physical monitor, resilient to hotplug and
/// DPI/work-area changes.
/// </summary>
public sealed class TreeManagerTests
{
    private static FakeDisplay Display(int handle, int left, int top, int width, int height, bool primary = false) =>
        new(new IntPtr(handle), Rectangle.FromSize(left, top, width, height),
            Rectangle.FromSize(left, top, width, height), 1.0, primary);

    // Task 3.1/3.2 (MM-1): exactly one tree root per connected physical monitor.
    [Fact]
    public void Constructor_OnePerDisplay_CreatesDistinctEmptyTrees()
    {
        var primary = Display(1, 0, 0, 1920, 1080, primary: true);
        var secondary = Display(2, 1920, 0, 1280, 720);
        var manager = new TreeManager(new IDisplay[] { primary, secondary }, primary, new WindowRegistry());

        Assert.True(manager.TryGetTree(primary, out var primaryTree));
        Assert.True(manager.TryGetTree(secondary, out var secondaryTree));
        Assert.NotSame(primaryTree, secondaryTree);
        Assert.Null(primaryTree!.Root);
        Assert.Null(secondaryTree!.Root);
    }

    // Task 3.1/3.2 (MM-2): a newly-connected monitor gets a fresh, empty tree.
    [Fact]
    public void OnDisplayConnected_NewDisplay_CreatesEmptyTree()
    {
        var primary = Display(1, 0, 0, 1920, 1080, primary: true);
        var manager = new TreeManager(new IDisplay[] { primary }, primary, new WindowRegistry());
        var connected = Display(3, -1280, 0, 1280, 720);

        manager.OnDisplayConnected(connected);

        Assert.True(manager.TryGetTree(connected, out var tree));
        Assert.Null(tree!.Root);
    }

    // Triangulation: a duplicate/already-known connect notification must not wipe an existing
    // tree's windows (idempotent MM-2, no window loss).
    [Fact]
    public void OnDisplayConnected_AlreadyKnownDisplay_DoesNotReplaceExistingTree()
    {
        var primary = Display(1, 0, 0, 1920, 1080, primary: true);
        var manager = new TreeManager(new IDisplay[] { primary }, primary, new WindowRegistry());
        manager.TryGetTree(primary, out var before);
        var window = new RecordingWindow(new IntPtr(100), Rectangle.FromSize(0, 0, 800, 600));
        before!.Root = new LeafNode(new WindowRef(window.Handle));

        manager.OnDisplayConnected(primary);

        Assert.True(manager.TryGetTree(primary, out var after));
        Assert.Same(before, after);
        Assert.NotNull(after!.Root);
    }

    // Task 3.3/3.4 (MM-3 scenario "Disconnect reparents windows"): a secondary monitor's 3 tiled
    // windows all reparent into the primary tree with no crash or loss, preserving the exact
    // registered LeafNode instances (WindowRegistry must not need re-registration).
    [Fact]
    public void OnDisplayDisconnected_SecondaryWithWindows_ReparentsAllIntoPrimaryTree_NoLoss()
    {
        var primary = Display(1, 0, 0, 1920, 1080, primary: true);
        var secondary = Display(2, 1920, 0, 1280, 720);
        var registry = new WindowRegistry();
        var manager = new TreeManager(new IDisplay[] { primary, secondary }, primary, registry);
        manager.TryGetTree(secondary, out var secondaryTree);

        var w1 = new RecordingWindow(new IntPtr(10), Rectangle.FromSize(0, 0, 1280, 720));
        var leaf1 = new LeafNode(new WindowRef(w1.Handle));
        secondaryTree!.Root = leaf1;
        registry.Register(w1, leaf1);

        var w2 = new RecordingWindow(new IntPtr(20), Rectangle.FromSize(0, 0, 1280, 720));
        var group = LayoutTree.AddChild(leaf1, new WindowRef(w2.Handle), 1280, 720);
        secondaryTree.Root = group;
        var leaf2 = (LeafNode)group.Children[1];
        registry.Register(w2, leaf2);

        var w3 = new RecordingWindow(new IntPtr(30), Rectangle.FromSize(0, 0, 1280, 720));
        var leaf3 = new LeafNode(new WindowRef(w3.Handle));
        LayoutTree.AddChild(group, leaf3, group.Children.Count);
        registry.Register(w3, leaf3);

        manager.OnDisplayDisconnected(secondary, new Rect(0, 0, 1920, 1080));

        Assert.False(manager.TryGetTree(secondary, out _));
        manager.TryGetTree(primary, out var primaryTree);
        var primaryLeaves = CollectLeaves(primaryTree!.Root);
        Assert.Equal(3, primaryLeaves.Count);
        Assert.Contains(leaf1, primaryLeaves);
        Assert.Contains(leaf2, primaryLeaves);
        Assert.Contains(leaf3, primaryLeaves);
        Assert.True(registry.TryGetLeaf(w1.Handle, out var found1));
        Assert.Same(leaf1, found1);
        Assert.True(registry.TryGetLeaf(w2.Handle, out var found2));
        Assert.Same(leaf2, found2);
        Assert.True(registry.TryGetLeaf(w3.Handle, out var found3));
        Assert.Same(leaf3, found3);
    }

    // Verify-report precedent (WU7C part 2/WU7D): every tree mutation must re-arrange and
    // reposition via the shared TreeArranger, not just mutate the tree in memory.
    [Fact]
    public void OnDisplayDisconnected_SecondaryWithWindow_PositionsReparentedWindowInPrimaryWorkArea()
    {
        var primary = Display(1, 0, 0, 1920, 1080, primary: true);
        var secondary = Display(2, 1920, 0, 1280, 720);
        var registry = new WindowRegistry();
        var manager = new TreeManager(new IDisplay[] { primary, secondary }, primary, registry);
        manager.TryGetTree(secondary, out var secondaryTree);
        var window = new RecordingWindow(new IntPtr(40), Rectangle.FromSize(0, 0, 1280, 720));
        var leaf = new LeafNode(new WindowRef(window.Handle));
        secondaryTree!.Root = leaf;
        registry.Register(window, leaf);

        manager.OnDisplayDisconnected(secondary, new Rect(0, 0, 1920, 1080));

        Assert.Equal(Rectangle.FromSize(0, 0, 1920, 1080), window.LastSetPosition);
    }

    // Defensive no-op: an unknown display handle (already removed, or never registered) must not
    // throw -- mirrors WorkspaceSessionAdapter's stale/unknown-handle guard convention.
    [Fact]
    public void OnDisplayDisconnected_UnknownDisplay_NoOp()
    {
        var primary = Display(1, 0, 0, 1920, 1080, primary: true);
        var manager = new TreeManager(new IDisplay[] { primary }, primary, new WindowRegistry());
        var unknown = Display(99, 0, 0, 100, 100);

        manager.OnDisplayDisconnected(unknown, new Rect(0, 0, 1920, 1080));

        Assert.True(manager.TryGetTree(primary, out _));
    }

    // MM-3 does not define disconnecting the CURRENT primary (its own scenario only covers a
    // secondary monitor) -- documented as a deliberate, explicit boundary rather than a silent
    // guess: the caller must call SetPrimary with the OS-reassigned new primary first.
    [Fact]
    public void OnDisplayDisconnected_CurrentPrimary_ThrowsInvalidOperationException()
    {
        var primary = Display(1, 0, 0, 1920, 1080, primary: true);
        var manager = new TreeManager(new IDisplay[] { primary }, primary, new WindowRegistry());

        Assert.Throws<InvalidOperationException>(
            () => manager.OnDisplayDisconnected(primary, new Rect(0, 0, 1920, 1080)));
    }

    // Task 3.5/3.6 (MM-4 scenario "Work-area change reflows locally"): only the changed
    // monitor's tree reflows; other monitors are unaffected (no window loss on either).
    [Fact]
    public void OnDisplayChanged_ReflowsOnlyThatMonitorsTree_LeavesOtherMonitorUntouched()
    {
        var primary = Display(1, 0, 0, 1920, 1080, primary: true);
        var secondary = Display(2, 1920, 0, 1280, 720);
        var registry = new WindowRegistry();
        var manager = new TreeManager(new IDisplay[] { primary, secondary }, primary, registry);
        manager.TryGetTree(primary, out var primaryTree);
        manager.TryGetTree(secondary, out var secondaryTree);
        var primaryWindow = new RecordingWindow(new IntPtr(200), Rectangle.FromSize(0, 0, 1920, 1080));
        primaryTree!.Root = new LeafNode(new WindowRef(primaryWindow.Handle));
        registry.Register(primaryWindow, (LeafNode)primaryTree.Root);
        var secondaryWindow = new RecordingWindow(new IntPtr(210), Rectangle.FromSize(0, 0, 1280, 720));
        secondaryTree!.Root = new LeafNode(new WindowRef(secondaryWindow.Handle));
        registry.Register(secondaryWindow, (LeafNode)secondaryTree.Root);

        manager.OnDisplayChanged(primary, new Rect(0, 40, 1920, 1000));

        Assert.Equal(1, primaryWindow.SetPositionCallCount);
        Assert.Equal(Rectangle.FromSize(0, 40, 1920, 1000), primaryWindow.LastSetPosition);
        Assert.Equal(0, secondaryWindow.SetPositionCallCount);
    }

    // Task 3.7/3.8 (MM-5 scenario "Focus falls through to adjacent monitor"): the nearest
    // connected monitor in the requested direction resolves to its tree's first leaf.
    [Fact]
    public void FocusAdjacentDisplay_MonitorToRightExists_ReturnsFirstLeafOfThatMonitorsTree()
    {
        var primary = Display(1, 0, 0, 1920, 1080, primary: true);
        var secondary = Display(2, 1920, 0, 1280, 720);
        var manager = new TreeManager(new IDisplay[] { primary, secondary }, primary, new WindowRegistry());
        manager.TryGetTree(secondary, out var secondaryTree);
        var leaf = new LeafNode(new WindowRef(new IntPtr(300)));
        secondaryTree!.Root = leaf;

        var result = manager.FocusAdjacentDisplay(primary, Direction.Right);

        Assert.Equal(FocusWalkStatus.Found, result.Status);
        Assert.Same(leaf, result.Leaf);
    }

    // MM-5 scenario "Focus falls through to adjacent monitor" no-op case: leftmost monitor,
    // Alt+H (Left) -- no monitor further left exists.
    [Theory]
    [InlineData(Direction.Left)] // no monitor further left of the primary
    public void FocusAdjacentDisplay_NoMonitorInDirection_ReturnsNoMatch(Direction direction)
    {
        var primary = Display(1, 0, 0, 1920, 1080, primary: true);
        var secondary = Display(2, 1920, 0, 1280, 720);
        var manager = new TreeManager(new IDisplay[] { primary, secondary }, primary, new WindowRegistry());

        var result = manager.FocusAdjacentDisplay(primary, direction);

        Assert.Equal(FocusWalkStatus.NoMatch, result.Status);
    }

    // Triangulation: an adjacent monitor exists but its tree is empty -- must not crash, and
    // must not resolve to a match (there is nothing to descend into).
    [Fact]
    public void FocusAdjacentDisplay_AdjacentMonitorTreeEmpty_ReturnsNoMatch()
    {
        var primary = Display(1, 0, 0, 1920, 1080, primary: true);
        var secondary = Display(2, 1920, 0, 1280, 720);
        var manager = new TreeManager(new IDisplay[] { primary, secondary }, primary, new WindowRegistry());

        Assert.Equal(FocusWalkStatus.NoMatch, manager.FocusAdjacentDisplay(primary, Direction.Right).Status);
    }

    private static List<LeafNode> CollectLeaves(Node? node) => node switch
    {
        null => [],
        LeafNode leaf => [leaf],
        GroupNode group => group.Children.SelectMany(CollectLeaves).ToList(),
        _ => []
    };
}
