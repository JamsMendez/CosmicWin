using CosmicWin.App.Input;
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

    // Verify-report #21 V19-W1 (probes 1-3): tree MEMBERSHIP stayed correct after a same-monitor
    // drag, but sibling ORDER can disagree with screen order, and NextFocus is tree-ordinal (LE-2).
    // windowA drags from the LEFT slot past windowB, to x=1500, ending up genuinely on the RIGHT.
    private static (Setup S, RecordingWindow A, RecordingWindow B) DragPastSibling(nint handleA, nint handleB)
    {
        var s = OneDisplay();
        _ = new MultiMonitorWorkspaceAdapter(s.Workspace, s.Trees, s.Registry, () => ExceptionList.Empty, () => false);
        var windowA = new RecordingWindow(handleA, Rectangle.FromSize(100, 100, 400, 300));
        var windowB = new RecordingWindow(handleB, Rectangle.FromSize(100, 100, 400, 300));
        s.Workspace.RaiseWindowAdded(windowA);
        s.Workspace.RaiseWindowAdded(windowB);
        windowA.SimulateExternalMove(Rectangle.FromSize(1500, 100, 400, 300));
        s.Workspace.RaiseWindowBoundsChanged(windowA);
        return (s, windowA, windowB);
    }

    [Fact]
    public void WindowBoundsChanged_SameMonitorDrag_ReordersTheGroup_AndReflowsBothWindowsToTheirNewSlots()
    {
        var (s, windowA, windowB) = DragPastSibling(new IntPtr(1001), new IntPtr(1002));
        s.Trees.TryGetTree(s.Primary, out var tree);
        var group = Assert.IsType<GroupNode>(tree!.Root);
        Assert.Equal(new WindowRef(windowB.Handle), Assert.IsType<LeafNode>(group.Children[0]).Window);
        Assert.Equal(new WindowRef(windowA.Handle), Assert.IsType<LeafNode>(group.Children[1]).Window);
        Assert.Equal(Rectangle.FromSize(0, 0, 960, 1080), windowB.LastSetPosition);
        Assert.Equal(Rectangle.FromSize(960, 0, 960, 1080), windowA.LastSetPosition);
    }

    // Before this fix, tree order stayed [A, B], so FocusLeft from B wrongly walked to A --
    // physically on the RIGHT after the drag. After the fix, B is genuinely leftmost, so FocusLeft
    // from B must find no match (LE-2 step 4), never activate the window on the right.
    [Fact]
    public async Task WindowBoundsChanged_SameMonitorDrag_ThenFocusLeftFromNowLeftmostWindow_ActivatesNothing()
    {
        var (s, windowA, windowB) = DragPastSibling(new IntPtr(1101), new IntPtr(1102));
        var executor = new ActionExecutor(new LayoutTree(), s.Registry, new FakeForegroundWindowSource { Handle = windowB.Handle }) { TreeManager = s.Trees };
        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.FocusLeft), CancellationToken.None);
        Assert.Equal(0, windowA.TryActivateCallCount);
        Assert.Equal(0, windowB.TryActivateCallCount);
    }

    // Before this fix, FocusRight from B (tree index 1, the LAST slot) could never reach A, even
    // though A ends up physically on the right after the drag. After the fix, tree order agrees
    // with screen order, so FocusRight from B must reach A.
    [Fact]
    public async Task WindowBoundsChanged_SameMonitorDrag_ThenFocusRightFromNowLeftmostWindow_ActivatesTheWindowOnTheRight()
    {
        var (s, windowA, windowB) = DragPastSibling(new IntPtr(1201), new IntPtr(1202));
        var executor = new ActionExecutor(new LayoutTree(), s.Registry, new FakeForegroundWindowSource { Handle = windowB.Handle }) { TreeManager = s.Trees };
        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.FocusRight), CancellationToken.None);
        Assert.Equal(1, windowA.TryActivateCallCount);
        Assert.Equal(0, windowB.TryActivateCallCount);
    }

    // Decision #76: a drag while paused is a full no-op on the same-monitor path too.
    [Fact]
    public void WindowBoundsChanged_SameMonitorDrag_WhilePaused_DoesNotReorderOrReflow()
    {
        var s = OneDisplay();
        var paused = false;
        using var adapter = new MultiMonitorWorkspaceAdapter(s.Workspace, s.Trees, s.Registry, () => ExceptionList.Empty, () => paused);
        var windowA = new RecordingWindow(new IntPtr(1301), Rectangle.FromSize(100, 100, 400, 300));
        var windowB = new RecordingWindow(new IntPtr(1302), Rectangle.FromSize(100, 100, 400, 300));
        s.Workspace.RaiseWindowAdded(windowA);
        s.Workspace.RaiseWindowAdded(windowB);
        var setCountA = windowA.SetPositionCallCount;
        paused = true;
        windowA.SimulateExternalMove(Rectangle.FromSize(1500, 100, 400, 300));
        s.Workspace.RaiseWindowBoundsChanged(windowA);
        s.Trees.TryGetTree(s.Primary, out var tree);
        var group = Assert.IsType<GroupNode>(tree!.Root);
        Assert.Equal(new WindowRef(windowA.Handle), Assert.IsType<LeafNode>(group.Children[0]).Window);
        Assert.Equal(new WindowRef(windowB.Handle), Assert.IsType<LeafNode>(group.Children[1]).Window);
        Assert.Equal(setCountA, windowA.SetPositionCallCount);
    }

    // Verify-report #21 V19-W2: pins the same-display guard. A flat 3-child group distinguishes
    // in-place reorder (dragged window lands wherever its new screen position says) from the
    // remove-then-append-at-the-end cross-monitor path (always drops it last).
    [Fact]
    public void WindowBoundsChanged_SameMonitorDrag_MiddleWindowDraggedToTheFront_ReordersInPlace_NotAppendedAtTheEnd()
    {
        var s = OneDisplay();
        using var adapter = new MultiMonitorWorkspaceAdapter(s.Workspace, s.Trees, s.Registry, () => ExceptionList.Empty, () => false);
        var windowA = new RecordingWindow(new IntPtr(1401), Rectangle.FromSize(100, 100, 400, 300));
        var windowB = new RecordingWindow(new IntPtr(1402), Rectangle.FromSize(100, 100, 400, 300));
        var windowC = new RecordingWindow(new IntPtr(1403), Rectangle.FromSize(100, 100, 400, 300));
        s.Workspace.RaiseWindowAdded(windowA);
        s.Workspace.RaiseWindowAdded(windowB);
        s.Workspace.RaiseWindowAdded(windowC);
        windowB.SimulateExternalMove(Rectangle.FromSize(-500, 100, 400, 300));
        s.Workspace.RaiseWindowBoundsChanged(windowB);
        s.Trees.TryGetTree(s.Primary, out var tree);
        var group = Assert.IsType<GroupNode>(tree!.Root);
        Assert.Equal(3, group.Children.Count);
        Assert.Equal(new WindowRef(windowB.Handle), Assert.IsType<LeafNode>(group.Children[0]).Window);
        Assert.Equal(new WindowRef(windowA.Handle), Assert.IsType<LeafNode>(group.Children[1]).Window);
        Assert.Equal(new WindowRef(windowC.Handle), Assert.IsType<LeafNode>(group.Children[2]).Window);
    }

    // Verify-report #21 V19-W3: pins Dispose's WindowBoundsChanged unsubscription (the existing
    // Dispose test below covers only WindowAdded).
    [Fact]
    public void Dispose_UnsubscribesFromWindowBoundsChanged_LaterDragIsIgnored()
    {
        var s = OneDisplay();
        var adapter = new MultiMonitorWorkspaceAdapter(s.Workspace, s.Trees, s.Registry, () => ExceptionList.Empty, () => false);
        var windowA = new RecordingWindow(new IntPtr(1501), Rectangle.FromSize(100, 100, 400, 300));
        var windowB = new RecordingWindow(new IntPtr(1502), Rectangle.FromSize(100, 100, 400, 300));
        s.Workspace.RaiseWindowAdded(windowA);
        s.Workspace.RaiseWindowAdded(windowB);
        adapter.Dispose();
        windowA.SimulateExternalMove(Rectangle.FromSize(1500, 100, 400, 300));
        s.Workspace.RaiseWindowBoundsChanged(windowA);
        s.Trees.TryGetTree(s.Primary, out var tree);
        var group = Assert.IsType<GroupNode>(tree!.Root);
        Assert.Equal(new WindowRef(windowA.Handle), Assert.IsType<LeafNode>(group.Children[0]).Window);
        Assert.Equal(new WindowRef(windowB.Handle), Assert.IsType<LeafNode>(group.Children[1]).Window);
    }

    private sealed class FakeForegroundWindowSource : IForegroundWindowSource
    {
        public nint Handle { get; set; }

        public nint GetForegroundHandle() => Handle;
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
