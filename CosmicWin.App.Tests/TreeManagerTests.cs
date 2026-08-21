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

    // Verify-report #21 rev 7 WARNING W1: SetPrimary(IDisplay) had zero test coverage. This is
    // the natural "SetPrimary then disconnect" interaction the report flagged as missing --
    // after SetPrimary reassigns which display is treated as primary, the FORMER primary is no
    // longer protected and its windows reparent into the NEW primary's tree.
    [Fact]
    public void SetPrimary_ThenDisconnectFormerPrimary_ReparentsWindowsIntoNewPrimaryTree()
    {
        var originalPrimary = Display(1, 0, 0, 1920, 1080, primary: true);
        var newPrimary = Display(2, 1920, 0, 1280, 720);
        var registry = new WindowRegistry();
        var manager = new TreeManager(new IDisplay[] { originalPrimary, newPrimary }, originalPrimary, registry);
        manager.TryGetTree(originalPrimary, out var originalPrimaryTree);
        var window = new RecordingWindow(new IntPtr(500), Rectangle.FromSize(0, 0, 1920, 1080));
        var leaf = new LeafNode(new WindowRef(window.Handle));
        originalPrimaryTree!.Root = leaf;
        registry.Register(window, leaf);

        manager.SetPrimary(newPrimary);
        manager.OnDisplayDisconnected(originalPrimary, new Rect(1920, 0, 1280, 720));

        Assert.False(manager.TryGetTree(originalPrimary, out _));
        Assert.True(manager.TryGetTree(newPrimary, out var newPrimaryTree));
        var newPrimaryLeaves = CollectLeaves(newPrimaryTree!.Root);
        Assert.Single(newPrimaryLeaves);
        Assert.Same(leaf, newPrimaryLeaves[0]);
        Assert.True(registry.TryGetLeaf(window.Handle, out var found));
        Assert.Same(leaf, found);
    }

    // Triangulation for W1: the mirror direction of the same interaction. After SetPrimary
    // reassigns primary, the NEW primary is now the protected display -- disconnecting it must
    // throw, proving SetPrimary genuinely moved the protection, not just permitted the old one.
    [Fact]
    public void SetPrimary_ThenDisconnectNewPrimary_ThrowsInvalidOperationException()
    {
        var originalPrimary = Display(1, 0, 0, 1920, 1080, primary: true);
        var newPrimary = Display(2, 1920, 0, 1280, 720);
        var manager = new TreeManager(new IDisplay[] { originalPrimary, newPrimary }, originalPrimary, new WindowRegistry());

        manager.SetPrimary(newPrimary);

        Assert.Throws<InvalidOperationException>(
            () => manager.OnDisplayDisconnected(newPrimary, new Rect(0, 0, 1920, 1080)));
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
    //
    // Verify-report #21 rev 7 WARNING W2: this was previously a [Theory] with a SINGLE
    // [InlineData(Direction.Left)], which delivers no more triangulation than a plain [Fact] --
    // the horizontal two-display layout below only naturally produces a genuine NoMatch for
    // Left when queried FROM the primary (Right from primary finds the secondary; Up/Down need
    // a vertically-separated layout entirely). Converted to a [Fact] because a single case is
    // genuinely sufficient for THIS layout/query-origin combination; real triangulation for the
    // other three directions is provided below via dedicated Right/Up/Down NoMatch tests that
    // each need a different layout or query origin to be a genuine (not vacuous) NoMatch.
    [Fact]
    public void FocusAdjacentDisplay_NoMonitorToLeft_ReturnsNoMatch()
    {
        var primary = Display(1, 0, 0, 1920, 1080, primary: true);
        var secondary = Display(2, 1920, 0, 1280, 720);
        var manager = new TreeManager(new IDisplay[] { primary, secondary }, primary, new WindowRegistry());

        var result = manager.FocusAdjacentDisplay(primary, Direction.Left);

        Assert.Equal(FocusWalkStatus.NoMatch, result.Status);
    }

    // Triangulation for W2 (Right): reuses the same horizontal layout as the Left no-match case
    // above, but queries FROM the rightmost display -- no monitor exists further right of it.
    [Fact]
    public void FocusAdjacentDisplay_NoMonitorToRight_ReturnsNoMatch()
    {
        var primary = Display(1, 0, 0, 1920, 1080, primary: true);
        var secondary = Display(2, 1920, 0, 1280, 720);
        var manager = new TreeManager(new IDisplay[] { primary, secondary }, primary, new WindowRegistry());

        var result = manager.FocusAdjacentDisplay(secondary, Direction.Right);

        Assert.Equal(FocusWalkStatus.NoMatch, result.Status);
    }

    // Verify-report #21 rev 7 WARNING W2: Direction.Up was never exercised by any test at all
    // (TreeManager.cs:184). Vertically stacked monitors are a normal configuration and MM-5 is
    // direction-agnostic, so this uses a dedicated top/bottom layout (a horizontal layout cannot
    // exercise Up/Down meaningfully). Queries FROM the bottom display -- the top display's tree
    // resolves as the adjacent match.
    [Fact]
    public void FocusAdjacentDisplay_MonitorAboveExists_ReturnsFirstLeafOfThatMonitorsTree()
    {
        var top = Display(1, 0, 0, 1920, 1080, primary: true);
        var bottom = Display(2, 0, 1080, 1920, 1080);
        var manager = new TreeManager(new IDisplay[] { top, bottom }, top, new WindowRegistry());
        manager.TryGetTree(top, out var topTree);
        var leaf = new LeafNode(new WindowRef(new IntPtr(600)));
        topTree!.Root = leaf;

        var result = manager.FocusAdjacentDisplay(bottom, Direction.Up);

        Assert.Equal(FocusWalkStatus.Found, result.Status);
        Assert.Same(leaf, result.Leaf);
    }

    // Triangulation for W2 (Up no-match): queries FROM the top display -- no monitor exists
    // above it in the same vertical layout.
    [Fact]
    public void FocusAdjacentDisplay_NoMonitorAbove_ReturnsNoMatch()
    {
        var top = Display(1, 0, 0, 1920, 1080, primary: true);
        var bottom = Display(2, 0, 1080, 1920, 1080);
        var manager = new TreeManager(new IDisplay[] { top, bottom }, top, new WindowRegistry());

        var result = manager.FocusAdjacentDisplay(top, Direction.Up);

        Assert.Equal(FocusWalkStatus.NoMatch, result.Status);
    }

    // Verify-report #21 rev 7 WARNING W2: Direction.Down was never exercised by any test at all
    // (TreeManager.cs:185). Queries FROM the top display -- the bottom display's tree resolves
    // as the adjacent match.
    [Fact]
    public void FocusAdjacentDisplay_MonitorBelowExists_ReturnsFirstLeafOfThatMonitorsTree()
    {
        var top = Display(1, 0, 0, 1920, 1080, primary: true);
        var bottom = Display(2, 0, 1080, 1920, 1080);
        var manager = new TreeManager(new IDisplay[] { top, bottom }, top, new WindowRegistry());
        manager.TryGetTree(bottom, out var bottomTree);
        var leaf = new LeafNode(new WindowRef(new IntPtr(700)));
        bottomTree!.Root = leaf;

        var result = manager.FocusAdjacentDisplay(top, Direction.Down);

        Assert.Equal(FocusWalkStatus.Found, result.Status);
        Assert.Same(leaf, result.Leaf);
    }

    // Triangulation for W2 (Down no-match): queries FROM the bottom display -- no monitor
    // exists below it in the same vertical layout.
    [Fact]
    public void FocusAdjacentDisplay_NoMonitorBelow_ReturnsNoMatch()
    {
        var top = Display(1, 0, 0, 1920, 1080, primary: true);
        var bottom = Display(2, 0, 1080, 1920, 1080);
        var manager = new TreeManager(new IDisplay[] { top, bottom }, top, new WindowRegistry());

        var result = manager.FocusAdjacentDisplay(bottom, Direction.Down);

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
