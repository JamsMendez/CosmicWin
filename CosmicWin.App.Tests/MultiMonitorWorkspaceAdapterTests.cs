using CosmicWin.App.Input;
using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App.Tests;

/// <summary>
/// The first real production caller of <see
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
        using var adapter = new MultiMonitorWorkspaceAdapter(s.Workspace, s.Trees, s.Registry, () => ExceptionList.Empty, () => false, () => null);
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
        using var adapter = new MultiMonitorWorkspaceAdapter(s.Workspace, s.Trees, s.Registry, () => ExceptionList.Empty, () => false, () => null);
        var onPrimary = new RecordingWindow(new IntPtr(40), Rectangle.FromSize(100, 100, 400, 300));
        s.Workspace.RaiseWindowAdded(onPrimary);
        var primarySetCountAfterAdd = onPrimary.SetPositionCallCount;
        var first = new RecordingWindow(new IntPtr(50), Rectangle.FromSize(2000, 100, 400, 300));
        var second = new RecordingWindow(new IntPtr(60), Rectangle.FromSize(2000, 100, 400, 300));
        s.Workspace.RaiseWindowAdded(first);
        s.Workspace.RaiseWindowAdded(second);

        s.Workspace.RaiseWindowRemoved(second);

        s.Trees.TryGetTree(s.Secondary, out var secondaryTree);
        // A group down to a single child is now collapsed on removal, so the survivor IS the
        // root rather than sitting inside a one-child wrapper. The wrapper was incidental
        // structure this fact never meant to pin; the survivor and its geometry are.
        Assert.Equal(new WindowRef(first.Handle), Assert.IsType<LeafNode>(secondaryTree!.Root).Window);
        Assert.Equal(Rectangle.FromSize(1920, 0, 1280, 720), first.LastSetPosition);
        Assert.Equal(primarySetCountAfterAdd, onPrimary.SetPositionCallCount); // primary untouched
    }

    [Fact]
    public void WindowAdded_WhilePaused_DoesNotTrackOrArrange()
    {
        var s = OneDisplay();
        using var adapter = new MultiMonitorWorkspaceAdapter(s.Workspace, s.Trees, s.Registry, () => ExceptionList.Empty, () => true, () => null);
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
        using var adapter = new MultiMonitorWorkspaceAdapter(s.Workspace, s.Trees, s.Registry, () => exceptions, () => false, () => null);
        var window = new RecordingWindow(new IntPtr(80), Rectangle.FromSize(100, 100, 400, 300), processName: "Excluded.exe");
        s.Workspace.RaiseWindowAdded(window);
        s.Trees.TryGetTree(s.Primary, out var tree);
        Assert.Null(tree!.Root);
    }

    // Removal always happens, only reflow is gated.
    [Fact]
    public void WindowRemoved_WhilePaused_RemovesFromRegistry_ButSkipsReflow()
    {
        var s = OneDisplay();
        var paused = false;
        using var adapter = new MultiMonitorWorkspaceAdapter(s.Workspace, s.Trees, s.Registry, () => ExceptionList.Empty, () => paused, () => null);
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

    // Decision #80: a window tiled on the SECONDARY
    // monitor is dragged onto the PRIMARY monitor -- since the tree never changes, it snaps BACK to
    // its ORIGINAL monitor's slot; the primary tree stays untouched.
    [Fact]
    public void WindowBoundsChanged_WindowDraggedFromSecondaryToPrimary_SnapsBackToOriginalMonitorSlot()
    {
        var s = TwoDisplays();
        using var adapter = new MultiMonitorWorkspaceAdapter(s.Workspace, s.Trees, s.Registry, () => ExceptionList.Empty, () => false, () => null);
        var windowA = new RecordingWindow(new IntPtr(801), Rectangle.FromSize(2000, 100, 400, 300));
        var windowB = new RecordingWindow(new IntPtr(802), Rectangle.FromSize(2000, 100, 400, 300));
        s.Workspace.RaiseWindowAdded(windowA);
        s.Workspace.RaiseWindowAdded(windowB);
        var originalPositionB = windowB.LastSetPosition;

        windowB.SimulateExternalMove(Rectangle.FromSize(100, 100, 400, 300));
        s.Workspace.RaiseWindowBoundsChanged(windowB);

        s.Trees.TryGetTree(s.Primary, out var primaryTree);
        s.Trees.TryGetTree(s.Secondary, out var secondaryTree);
        Assert.Null(primaryTree!.Root); // never re-homed to primary
        var secondaryGroup = Assert.IsType<GroupNode>(secondaryTree!.Root);
        Assert.Equal(2, secondaryGroup.Children.Count);
        Assert.Equal(new WindowRef(windowA.Handle), Assert.IsType<LeafNode>(secondaryGroup.Children[0]).Window);
        Assert.Equal(new WindowRef(windowB.Handle), Assert.IsType<LeafNode>(secondaryGroup.Children[1]).Window);
        Assert.Equal(originalPositionB, windowB.LastSetPosition); // snapped back on screen
        Assert.Equal(3, windowA.SetPositionCallCount); // pins a genuine re-arrange, not a value that already matched
        Assert.Equal(2, windowB.SetPositionCallCount);
    }

    /// <summary>
    /// Measured on real hardware: a MINIMIZED window was admitted into the tree and held
    /// a whole tile, so the one visible window only got half the screen. The filter now rejects
    /// WS_MINIMIZE, but that alone only covers windows already minimized at startup -- exclusion is
    /// evaluated in OnWindowAdded and never again. A window minimized WHILE tiled kept its tile.
    /// Minimizing moves the window to (-32000,-32000), which raises the very bounds-changed event
    /// this handler already listens to, so that is where the re-check belongs.
    /// </summary>
    [Fact]
    public void WindowBoundsChanged_TiledWindowIsMinimized_GivesItsTileBackToTheSurvivor()
    {
        var s = OneDisplay();
        using var adapter = new MultiMonitorWorkspaceAdapter(s.Workspace, s.Trees, s.Registry, () => ExceptionList.Empty, () => false, () => null);
        var stays = new RecordingWindow(new IntPtr(901), Rectangle.FromSize(0, 0, 400, 300));
        var minimized = new RecordingWindow(new IntPtr(902), Rectangle.FromSize(0, 0, 400, 300));
        s.Workspace.RaiseWindowAdded(stays);
        s.Workspace.RaiseWindowAdded(minimized);

        minimized.SimulateMinimize();
        s.Workspace.RaiseWindowBoundsChanged(minimized);

        s.Trees.TryGetTree(s.Primary, out var tree);
        var survivor = Assert.IsType<LeafNode>(tree!.Root);
        Assert.Equal(new WindowRef(stays.Handle), survivor.Window);
        Assert.False(s.Registry.TryGetLeaf(minimized.Handle, out _));
    }

    /// <summary>
    /// The other half: WS_MINIMIZE is TRANSIENT, so excluding on it without ever re-admitting would
    /// mean a window minimized once never tiles again until CosmicWin restarts. Restoring clears the
    /// bit and raises the same bounds-changed event, which is the moment to let it back in.
    /// </summary>
    [Fact]
    public void WindowBoundsChanged_MinimizedWindowIsRestored_IsAdmittedIntoTheTree()
    {
        var s = OneDisplay();
        using var adapter = new MultiMonitorWorkspaceAdapter(s.Workspace, s.Trees, s.Registry, () => ExceptionList.Empty, () => false, () => null);
        var tiled = new RecordingWindow(new IntPtr(911), Rectangle.FromSize(0, 0, 400, 300));
        var minimized = new RecordingWindow(new IntPtr(912), Rectangle.FromSize(0, 0, 400, 300));
        minimized.SimulateMinimize();
        s.Workspace.RaiseWindowAdded(tiled);
        s.Workspace.RaiseWindowAdded(minimized);

        // Precondition: it was refused on arrival, exactly as the real desktop snapshot showed.
        Assert.False(s.Registry.TryGetLeaf(minimized.Handle, out _));

        minimized.SimulateRestore(Rectangle.FromSize(0, 0, 400, 300));
        s.Workspace.RaiseWindowBoundsChanged(minimized);

        Assert.True(s.Registry.TryGetLeaf(minimized.Handle, out var leaf));
        Assert.NotNull(leaf);
        s.Trees.TryGetTree(s.Primary, out var tree);
        var group = Assert.IsType<GroupNode>(tree!.Root);
        Assert.Equal(2, group.Children.Count);
    }

    // Decision #76 ("full pause, no reconcile"): a drag while paused must not trigger a snap-back,
    // the same way OnWindowAdded and OnWindowRemoved already do not.
    [Fact]
    public void WindowBoundsChanged_WhilePaused_DoesNotSnapBackOrReflow()
    {
        var s = TwoDisplays();
        var paused = false;
        using var adapter = new MultiMonitorWorkspaceAdapter(s.Workspace, s.Trees, s.Registry, () => ExceptionList.Empty, () => paused, () => null);
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

    // Shared setup: two windows side by side, windowA dragged past windowB to x=1500 -- under
    // an earlier decision the tree never updates, so the drag is always undone on drop.
    private static (Setup S, RecordingWindow A, RecordingWindow B) DragPastSibling(nint handleA, nint handleB)
    {
        var s = OneDisplay();
        _ = new MultiMonitorWorkspaceAdapter(s.Workspace, s.Trees, s.Registry, () => ExceptionList.Empty, () => false, () => null);
        var windowA = new RecordingWindow(handleA, Rectangle.FromSize(100, 100, 400, 300));
        var windowB = new RecordingWindow(handleB, Rectangle.FromSize(100, 100, 400, 300));
        s.Workspace.RaiseWindowAdded(windowA);
        s.Workspace.RaiseWindowAdded(windowB);
        windowA.SimulateExternalMove(Rectangle.FromSize(1500, 100, 400, 300));
        s.Workspace.RaiseWindowBoundsChanged(windowA);
        return (s, windowA, windowB);
    }

    // Decision #80 (supersedes 's/'s "tree follows window" rule, never put to the user): a
    // dragged tiled window snaps BACK to its tree slot -- RED against code, which would instead
    // leave A at the dragged position and swap tree order.
    [Fact]
    public void WindowBoundsChanged_DraggedWindowOnSameMonitor_SnapsBackToTreeSlot_TreeOrderUnchanged()
    {
        var (s, windowA, windowB) = DragPastSibling(new IntPtr(1001), new IntPtr(1002));
        s.Trees.TryGetTree(s.Primary, out var tree);
        var group = Assert.IsType<GroupNode>(tree!.Root);
        Assert.Equal(new WindowRef(windowA.Handle), Assert.IsType<LeafNode>(group.Children[0]).Window);
        Assert.Equal(new WindowRef(windowB.Handle), Assert.IsType<LeafNode>(group.Children[1]).Window);
        Assert.Equal(Rectangle.FromSize(0, 0, 960, 1080), windowA.LastSetPosition);
        Assert.Equal(Rectangle.FromSize(960, 0, 960, 1080), windowB.LastSetPosition);
        Assert.Equal(3, windowA.SetPositionCallCount); // pins a genuine re-arrange, not a value that already matched
        Assert.Equal(2, windowB.SetPositionCallCount);
    }

    // Decision #80: since a drag is always snapped back,
    // tree order can never disagree with screen order, so directional focus behaves as if the drag
    // never happened, with no reorder logic needed to keep it correct.
    [Fact]
    public async Task WindowBoundsChanged_SameMonitorDrag_ThenFocusRight_StillActivatesTheOriginalRightWindow()
    {
        var (s, windowA, windowB) = DragPastSibling(new IntPtr(1101), new IntPtr(1102));
        var executor = new ActionExecutor(new LayoutTree(), s.Registry, new FakeForegroundWindowSource { Handle = windowA.Handle }) { TreeManager = s.Trees };
        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.FocusRight), CancellationToken.None);
        Assert.Equal(1, windowB.TryActivateCallCount);
        Assert.Equal(0, windowA.TryActivateCallCount);
    }

    // Pins Dispose's unsubscription AND the constructor's subscription: deleting either line goes
    // RED, since SetPosition is never called again after Dispose to undo the post-dispose drag.
    [Fact]
    public void Dispose_UnsubscribesFromWindowBoundsChanged_LaterDragIsIgnored()
    {
        var s = OneDisplay();
        var adapter = new MultiMonitorWorkspaceAdapter(s.Workspace, s.Trees, s.Registry, () => ExceptionList.Empty, () => false, () => null);
        var windowA = new RecordingWindow(new IntPtr(1501), Rectangle.FromSize(100, 100, 400, 300));
        var windowB = new RecordingWindow(new IntPtr(1502), Rectangle.FromSize(100, 100, 400, 300));
        s.Workspace.RaiseWindowAdded(windowA);
        s.Workspace.RaiseWindowAdded(windowB);
        var setCountBeforeDispose = windowA.SetPositionCallCount;
        adapter.Dispose();

        windowA.SimulateExternalMove(Rectangle.FromSize(1500, 100, 400, 300));
        s.Workspace.RaiseWindowBoundsChanged(windowA);

        Assert.Equal(setCountBeforeDispose, windowA.SetPositionCallCount);
        Assert.Equal(Rectangle.FromSize(1500, 100, 400, 300), windowA.Bounds); // never snapped back
    }

    private sealed class FakeForegroundWindowSource : IForegroundWindowSource
    {
        public nint Handle { get; set; }

        public nint GetForegroundHandle() => Handle;
    }

    // Reproduces the exact measured shape -- A dragged to
    // x=1500 past B at x=960, but A's own SetPosition call FAILS during the snap-back attempt
    // (IWindow.CanReposition flips false and never self-heals, per its documented contract). A
    // window in this state must be treated exactly like a WE-1 exclusion: evicted from the tree
    // rather than left permanently desynced from screen order. B reflows to fill the vacated slot.
    [Fact]
    public void WindowBoundsChanged_WindowRefusesReposition_IsEvictedFromTree_SiblingReflowsToFillSpace()
    {
        var s = OneDisplay();
        _ = new MultiMonitorWorkspaceAdapter(s.Workspace, s.Trees, s.Registry, () => ExceptionList.Empty, () => false, () => null);
        var windowA = new RecordingWindow(new IntPtr(2001), Rectangle.FromSize(100, 100, 400, 300));
        var windowB = new RecordingWindow(new IntPtr(2002), Rectangle.FromSize(100, 100, 400, 300));
        s.Workspace.RaiseWindowAdded(windowA);
        s.Workspace.RaiseWindowAdded(windowB);

        windowA.FailNextSetPosition();
        windowA.SimulateExternalMove(Rectangle.FromSize(1500, 100, 400, 300));
        s.Workspace.RaiseWindowBoundsChanged(windowA);

        s.Trees.TryGetTree(s.Primary, out var tree);
        // A group down to a single child is now collapsed on removal, so the survivor IS the
        // root rather than sitting inside a one-child wrapper. The wrapper was incidental
        // structure this fact never meant to pin; the survivor and its geometry are.
        Assert.Equal(new WindowRef(windowB.Handle), Assert.IsType<LeafNode>(tree!.Root).Window);
        Assert.False(windowA.CanReposition);
        Assert.Equal(Rectangle.FromSize(1500, 100, 400, 300), windowA.Bounds); // keeps the drag, never snapped back
        Assert.Equal(3, windowA.SetPositionCallCount); // add(1) + B-add reflow(2) + failed snap-back attempt(3), then never again
        Assert.Equal(Rectangle.FromSize(0, 0, 1920, 1080), windowB.LastSetPosition); // reflowed into A's vacated space
        Assert.Equal(3, windowB.SetPositionCallCount); // add(1) + A's snap-back reflow(2) + eviction reflow(3)
        Assert.False(s.Registry.TryGetLeaf(windowA.Handle, out _)); // untracked, like a WE-1 exclusion

        // Idempotence: a SECOND out-of-band move for the now-evicted A must be a full no-op --
        // proves it is genuinely untracked (owner entry gone), not just missing from the tree.
        windowA.SimulateExternalMove(Rectangle.FromSize(1600, 100, 400, 300));
        s.Workspace.RaiseWindowBoundsChanged(windowA);
        Assert.Equal(3, windowA.SetPositionCallCount);
    }

    // Closes the measured focus inversion directly -- since A is evicted from the tree
    // and registry, a FocusRight hotkey with A in the foreground must no-op (A can no longer be
    // resolved as focused), instead of misdirecting activation to B (which is now physically on
    // A's LEFT after the drag, not its right).
    [Fact]
    public async Task WindowBoundsChanged_WindowRefusesReposition_ThenFocusRight_IsNoOp_NeverMisdirectsToWrongSibling()
    {
        var s = OneDisplay();
        _ = new MultiMonitorWorkspaceAdapter(s.Workspace, s.Trees, s.Registry, () => ExceptionList.Empty, () => false, () => null);
        var windowA = new RecordingWindow(new IntPtr(2101), Rectangle.FromSize(100, 100, 400, 300));
        var windowB = new RecordingWindow(new IntPtr(2102), Rectangle.FromSize(100, 100, 400, 300));
        s.Workspace.RaiseWindowAdded(windowA);
        s.Workspace.RaiseWindowAdded(windowB);
        windowA.FailNextSetPosition();
        windowA.SimulateExternalMove(Rectangle.FromSize(1500, 100, 400, 300));
        s.Workspace.RaiseWindowBoundsChanged(windowA);

        var executor = new ActionExecutor(new LayoutTree(), s.Registry, new FakeForegroundWindowSource { Handle = windowA.Handle }) { TreeManager = s.Trees };
        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.FocusRight), CancellationToken.None);

        Assert.Equal(0, windowB.TryActivateCallCount);
        Assert.Equal(0, windowA.TryActivateCallCount);
    }

    // OnWindowAdded never checked
    // CanReposition, so a protected window pinned at x=1500 stayed in the tree even after its own
    // first positioning attempt failed -- a later sibling still tiled into the slot next to it,
    // and FocusRight from the untileable window misdirected to that sibling. Fixed at the shared
    // TreeArranger.ArrangeAndPosition choke point (all 8 call sites), not this one call site.
    [Fact]
    public void WindowAdded_WindowRefusesRepositionOnItsFirstArrange_IsEvictedFromTree_NeverCoexistsWithSibling()
    {
        var s = OneDisplay();
        _ = new MultiMonitorWorkspaceAdapter(s.Workspace, s.Trees, s.Registry, () => ExceptionList.Empty, () => false, () => null);
        var windowA = new RecordingWindow(new IntPtr(3001), Rectangle.FromSize(1500, 100, 400, 300));
        var windowB = new RecordingWindow(new IntPtr(3002), Rectangle.FromSize(100, 100, 400, 300));

        windowA.FailNextSetPosition();
        s.Workspace.RaiseWindowAdded(windowA);
        s.Workspace.RaiseWindowAdded(windowB);

        s.Trees.TryGetTree(s.Primary, out var tree);
        Assert.Equal(new WindowRef(windowB.Handle), Assert.IsType<LeafNode>(tree!.Root).Window);
        Assert.False(windowA.CanReposition);
        Assert.Equal(Rectangle.FromSize(1500, 100, 400, 300), windowA.Bounds); // never moved
        Assert.Equal(1, windowA.SetPositionCallCount); // exactly one failed attempt, never retried
        Assert.Equal(Rectangle.FromSize(0, 0, 1920, 1080), windowB.LastSetPosition); // gets the FULL area
        Assert.False(s.Registry.TryGetLeaf(windowA.Handle, out _)); // untracked, like a WE-1 exclusion
    }

    // Closes the measured focus inversion directly through the OnWindowAdded
    // path -- A can never be resolved as focused once evicted, so FocusRight must no-op.
    [Fact]
    public async Task WindowAdded_WindowRefusesRepositionOnItsFirstArrange_ThenFocusRight_IsNoOp_NeverMisdirectsToWrongSibling()
    {
        var s = OneDisplay();
        _ = new MultiMonitorWorkspaceAdapter(s.Workspace, s.Trees, s.Registry, () => ExceptionList.Empty, () => false, () => null);
        var windowA = new RecordingWindow(new IntPtr(3101), Rectangle.FromSize(1500, 100, 400, 300));
        var windowB = new RecordingWindow(new IntPtr(3102), Rectangle.FromSize(100, 100, 400, 300));
        windowA.FailNextSetPosition();
        s.Workspace.RaiseWindowAdded(windowA);
        s.Workspace.RaiseWindowAdded(windowB);

        var executor = new ActionExecutor(new LayoutTree(), s.Registry, new FakeForegroundWindowSource { Handle = windowA.Handle }) { TreeManager = s.Trees };
        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.FocusRight), CancellationToken.None);

        Assert.Equal(0, windowB.TryActivateCallCount);
        Assert.Equal(0, windowA.TryActivateCallCount);
    }

    [Fact]
    public void Dispose_UnsubscribesFromWorkspaceEvents_LaterAddIsIgnored()
    {
        var s = OneDisplay();
        var adapter = new MultiMonitorWorkspaceAdapter(s.Workspace, s.Trees, s.Registry, () => ExceptionList.Empty, () => false, () => null);
        adapter.Dispose();
        var window = new RecordingWindow(new IntPtr(110), Rectangle.FromSize(100, 100, 400, 300));
        s.Workspace.RaiseWindowAdded(window);
        s.Trees.TryGetTree(s.Primary, out var tree);
        Assert.Null(tree!.Root);
    }
}
