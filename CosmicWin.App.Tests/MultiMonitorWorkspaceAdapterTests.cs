using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App.Tests;

/// <summary>
/// WU17 (closes carried finding W3, spec MM-1/MM-4): the first real production caller of <see
/// cref="TreeManager"/>. Reuses <see cref="WorkspaceSessionAdapter"/>'s extracted <see
/// cref="WorkspaceSessionAdapter.InsertWindow"/>/<see cref="WorkspaceSessionAdapter.RemoveWindow"/>/
/// <see cref="WorkspaceSessionAdapter.IsExcluded"/> statics (so both classes can never drift), but
/// resolves each window's owning monitor via <see cref="TreeManager.ResolveDisplay"/> instead of a
/// single fixed tree.
/// </summary>
public sealed class MultiMonitorWorkspaceAdapterTests
{
    private sealed record Setup(TreeManager Trees, WindowRegistry Registry, FakeWorkspace Workspace, IDisplay Primary, IDisplay Secondary);

    private static FakeDisplay Display(int handle, int left, int top, int width, int height, bool primary = false) =>
        new(new IntPtr(handle), Rectangle.FromSize(left, top, width, height),
            Rectangle.FromSize(left, top, width, height), 1.0, primary);

    private static Setup TwoDisplays()
    {
        var primary = Display(1, 0, 0, 1920, 1080, primary: true);
        var secondary = Display(2, 1920, 0, 1280, 720);
        var registry = new WindowRegistry();
        var trees = new TreeManager(new IDisplay[] { primary, secondary }, primary, registry);
        return new Setup(trees, registry, new FakeWorkspace(), primary, secondary);
    }

    private static Setup OneDisplay()
    {
        var primary = Display(1, 0, 0, 1920, 1080, primary: true);
        var registry = new WindowRegistry();
        var trees = new TreeManager(new IDisplay[] { primary }, primary, registry);
        return new Setup(trees, registry, new FakeWorkspace(), primary, primary);
    }

    [Fact]
    public void WindowAdded_OnPrimaryAndSecondary_RouteToOwnTree_AndArrangeWithOwnWorkArea()
    {
        var s = TwoDisplays();
        using var adapter = new MultiMonitorWorkspaceAdapter(s.Workspace, s.Trees, s.Registry, () => ExceptionList.Empty, () => false);
        var onPrimary = new RecordingWindow(new IntPtr(10), Rectangle.FromSize(100, 100, 400, 300));
        var onSecondary = new RecordingWindow(new IntPtr(20), Rectangle.FromSize(2000, 100, 400, 300));
        s.Workspace.RaiseWindowAdded(onPrimary);
        s.Workspace.RaiseWindowAdded(onSecondary);

        s.Trees.TryGetTree(s.Primary, out var primaryTree);
        s.Trees.TryGetTree(s.Secondary, out var secondaryTree);
        Assert.Equal(new WindowRef(onPrimary.Handle), Assert.IsType<LeafNode>(primaryTree!.Root).Window);
        Assert.Equal(new WindowRef(onSecondary.Handle), Assert.IsType<LeafNode>(secondaryTree!.Root).Window);
        Assert.Equal(Rectangle.FromSize(1920, 0, 1280, 720), onSecondary.LastSetPosition);
    }

    [Fact]
    public void WindowRemoved_OnSecondary_ReflowsOnlySecondaryTree_PrimaryUntouched()
    {
        var s = TwoDisplays();
        using var adapter = new MultiMonitorWorkspaceAdapter(s.Workspace, s.Trees, s.Registry, () => ExceptionList.Empty, () => false);
        var onPrimary = new RecordingWindow(new IntPtr(40), Rectangle.FromSize(100, 100, 400, 300));
        s.Workspace.RaiseWindowAdded(onPrimary);
        var primarySetCountAfterAdd = onPrimary.SetPositionCallCount;
        var first = new RecordingWindow(new IntPtr(50), Rectangle.FromSize(2000, 100, 400, 300));
        var second = new RecordingWindow(new IntPtr(60), Rectangle.FromSize(2000, 100, 400, 300));
        s.Workspace.RaiseWindowAdded(first);
        s.Workspace.RaiseWindowAdded(second);

        s.Workspace.RaiseWindowRemoved(second);

        s.Trees.TryGetTree(s.Secondary, out var secondaryTree);
        var group = Assert.IsType<GroupNode>(secondaryTree!.Root);
        Assert.Equal(new WindowRef(first.Handle), Assert.IsType<LeafNode>(Assert.Single(group.Children)).Window);
        Assert.Equal(Rectangle.FromSize(1920, 0, 1280, 720), first.LastSetPosition);
        Assert.Equal(primarySetCountAfterAdd, onPrimary.SetPositionCallCount); // primary untouched
    }

    [Fact]
    public void WindowAdded_WhilePaused_DoesNotTrackOrArrange()
    {
        var s = OneDisplay();
        using var adapter = new MultiMonitorWorkspaceAdapter(s.Workspace, s.Trees, s.Registry, () => ExceptionList.Empty, () => true);
        var window = new RecordingWindow(new IntPtr(70), Rectangle.FromSize(100, 100, 400, 300));
        s.Workspace.RaiseWindowAdded(window);
        s.Trees.TryGetTree(s.Primary, out var tree);
        Assert.Null(tree!.Root);
        Assert.Equal(0, window.SetPositionCallCount);
    }

    [Fact]
    public void WindowAdded_ExcludedWindow_NotTracked()
    {
        var s = OneDisplay();
        var exceptions = new ExceptionList([new ExceptionRule(ExceptionRuleKind.ProcessName, "Excluded.exe")]);
        using var adapter = new MultiMonitorWorkspaceAdapter(s.Workspace, s.Trees, s.Registry, () => exceptions, () => false);
        var window = new RecordingWindow(new IntPtr(80), Rectangle.FromSize(100, 100, 400, 300), processName: "Excluded.exe");
        s.Workspace.RaiseWindowAdded(window);
        s.Trees.TryGetTree(s.Primary, out var tree);
        Assert.Null(tree!.Root);
    }

    // V11-W2 semantics (decision #64), re-proven here: removal always happens, only reflow is gated.
    [Fact]
    public void WindowRemoved_WhilePaused_RemovesFromRegistry_ButSkipsReflow()
    {
        var s = OneDisplay();
        var paused = false;
        using var adapter = new MultiMonitorWorkspaceAdapter(s.Workspace, s.Trees, s.Registry, () => ExceptionList.Empty, () => paused);
        var first = new RecordingWindow(new IntPtr(90), Rectangle.FromSize(100, 100, 400, 300));
        var second = new RecordingWindow(new IntPtr(100), Rectangle.FromSize(100, 100, 400, 300));
        s.Workspace.RaiseWindowAdded(first);
        s.Workspace.RaiseWindowAdded(second);
        var firstSetCountBeforePause = first.SetPositionCallCount;

        paused = true;
        s.Workspace.RaiseWindowRemoved(second);

        Assert.False(s.Registry.TryGetLeaf(second.Handle, out _));
        Assert.Equal(firstSetCountBeforePause, first.SetPositionCallCount);
    }

    // Verify-report #21 V18-W2: reproduces probe E's shape -- two windows tiled on the SECONDARY
    // monitor, one dragged (out-of-band, not by hotkey) onto the PRIMARY monitor. Before the fix,
    // no code path ever observed the drag, so the leaf stayed structurally rooted in the secondary
    // tree while its real Bounds said primary -- the exact desync that later reproduces V17-W1's
    // "tree mutates, nothing repositions" transcript on the next hotkey. This proves the drag alone
    // re-homes the leaf into the tree matching its real position and reflows BOTH affected trees.
    [Fact]
    public void WindowBoundsChanged_WindowDraggedFromSecondaryToPrimary_ReHomesLeaf_AndReflowsBothTrees()
    {
        var s = TwoDisplays();
        using var adapter = new MultiMonitorWorkspaceAdapter(s.Workspace, s.Trees, s.Registry, () => ExceptionList.Empty, () => false);
        var windowA = new RecordingWindow(new IntPtr(801), Rectangle.FromSize(2000, 100, 400, 300));
        var windowB = new RecordingWindow(new IntPtr(802), Rectangle.FromSize(2000, 100, 400, 300));
        s.Workspace.RaiseWindowAdded(windowA);
        s.Workspace.RaiseWindowAdded(windowB);

        windowB.SimulateExternalMove(Rectangle.FromSize(100, 100, 400, 300));
        s.Workspace.RaiseWindowBoundsChanged(windowB);

        s.Trees.TryGetTree(s.Primary, out var primaryTree);
        s.Trees.TryGetTree(s.Secondary, out var secondaryTree);
        Assert.Equal(new WindowRef(windowB.Handle), Assert.IsType<LeafNode>(primaryTree!.Root).Window);
        var secondaryGroup = Assert.IsType<GroupNode>(secondaryTree!.Root);
        Assert.Equal(new WindowRef(windowA.Handle), Assert.IsType<LeafNode>(Assert.Single(secondaryGroup.Children)).Window);
        Assert.Equal(Rectangle.FromSize(0, 0, 1920, 1080), windowB.LastSetPosition);
        Assert.Equal(Rectangle.FromSize(1920, 0, 1280, 720), windowA.LastSetPosition);
    }

    // Decision #64 ("full pause, no reconcile"): a drag while paused must not trigger a reflow, the
    // same way OnWindowAdded and OnWindowRemoved already do not (V18-W2 fix design note).
    [Fact]
    public void WindowBoundsChanged_WhilePaused_DoesNotReHomeOrReflow()
    {
        var s = TwoDisplays();
        var paused = false;
        using var adapter = new MultiMonitorWorkspaceAdapter(s.Workspace, s.Trees, s.Registry, () => ExceptionList.Empty, () => paused);
        var windowA = new RecordingWindow(new IntPtr(901), Rectangle.FromSize(2000, 100, 400, 300));
        s.Workspace.RaiseWindowAdded(windowA);
        var setCountBeforeDrag = windowA.SetPositionCallCount;

        paused = true;
        windowA.SimulateExternalMove(Rectangle.FromSize(100, 100, 400, 300));
        s.Workspace.RaiseWindowBoundsChanged(windowA);

        s.Trees.TryGetTree(s.Primary, out var primaryTree);
        s.Trees.TryGetTree(s.Secondary, out var secondaryTree);
        Assert.Null(primaryTree!.Root);
        Assert.Equal(new WindowRef(windowA.Handle), Assert.IsType<LeafNode>(secondaryTree!.Root).Window);
        Assert.Equal(setCountBeforeDrag, windowA.SetPositionCallCount);
    }

    [Fact]
    public void Dispose_UnsubscribesFromWorkspaceEvents_LaterAddIsIgnored()
    {
        var s = OneDisplay();
        var adapter = new MultiMonitorWorkspaceAdapter(s.Workspace, s.Trees, s.Registry, () => ExceptionList.Empty, () => false);
        adapter.Dispose();
        var window = new RecordingWindow(new IntPtr(110), Rectangle.FromSize(100, 100, 400, 300));
        s.Workspace.RaiseWindowAdded(window);
        s.Trees.TryGetTree(s.Primary, out var tree);
        Assert.Null(tree!.Root);
    }
}
