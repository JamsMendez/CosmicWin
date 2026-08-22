using CosmicWin.App.Input;
using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;
using CosmicWin.Layout;

namespace CosmicWin.App.Tests;

/// <summary>
/// Task 2.13: all HA-1 categories (Focus/Move/Orientation/Resize), foreground lookup, focus
/// activation, Rect↔Rectangle conversion, one <see cref="IWindow.SetPosition"/> per live leaf,
/// and protected-window no-retry.
/// </summary>
public sealed class ActionExecutorTests
{
    private static (
        ActionExecutor Executor,
        FakeForegroundWindowSource Foreground,
        WindowRegistry Registry,
        RecordingWindow WindowA,
        RecordingWindow WindowB,
        RecordingWindow WindowC) BuildThreeLeafRow()
    {
        var leafA = new LeafNode(new WindowRef(new IntPtr(1)));
        var leafB = new LeafNode(new WindowRef(new IntPtr(2)));
        var leafC = new LeafNode(new WindowRef(new IntPtr(3)));
        var group = new GroupNode(SplitAxis.Horizontal) { GroupLength = 900 };
        group.Children.Add(leafA);
        group.Children.Add(leafB);
        group.Children.Add(leafC);
        group.Sizes.Add(300);
        group.Sizes.Add(300);
        group.Sizes.Add(300);
        leafA.Parent = group;
        leafB.Parent = group;
        leafC.Parent = group;

        ITilingEngine engine = new LayoutTree(group);
        var registry = new WindowRegistry();
        var windowA = new RecordingWindow(leafA.Window.Handle, Rectangle.Empty);
        var windowB = new RecordingWindow(leafB.Window.Handle, Rectangle.Empty);
        var windowC = new RecordingWindow(leafC.Window.Handle, Rectangle.Empty);
        registry.Register(windowA, leafA);
        registry.Register(windowB, leafB);
        registry.Register(windowC, leafC);

        var foreground = new FakeForegroundWindowSource { Handle = windowA.Handle };
        var executor = new ActionExecutor(engine, registry, foreground) { WorkArea = new Rect(0, 0, 900, 100) };

        return (executor, foreground, registry, windowA, windowB, windowC);
    }

    [Fact]
    public async Task ScheduleAsync_MoveRight_ResolvesFocusViaForegroundLookup_SwapsAndRepositionsEachLiveLeafOnce()
    {
        var (executor, _, _, windowA, windowB, windowC) = BuildThreeLeafRow();

        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.MoveRight), CancellationToken.None);

        Assert.Equal(1, windowA.SetPositionCallCount);
        Assert.Equal(1, windowB.SetPositionCallCount);
        Assert.Equal(1, windowC.SetPositionCallCount);
        Assert.Equal(Rectangle.FromSize(300, 0, 300, 100), windowA.LastSetPosition);
        Assert.Equal(Rectangle.FromSize(0, 0, 300, 100), windowB.LastSetPosition);
        Assert.Equal(Rectangle.FromSize(600, 0, 300, 100), windowC.LastSetPosition);
    }

    [Fact]
    public async Task ScheduleAsync_WhenForegroundUnresolvable_NoOps_WithoutThrowing()
    {
        var (executor, foreground, _, windowA, windowB, windowC) = BuildThreeLeafRow();
        foreground.Handle = new IntPtr(404);

        var exception = await Record.ExceptionAsync(
            () => executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.MoveRight), CancellationToken.None).AsTask());

        Assert.Null(exception);
        Assert.Equal(0, windowA.SetPositionCallCount);
        Assert.Equal(0, windowB.SetPositionCallCount);
        Assert.Equal(0, windowC.SetPositionCallCount);
    }

    [Fact]
    public async Task ScheduleAsync_FocusRight_MovesFocusAndActivatesTheNewlyFocusedWindow_WithoutRearranging()
    {
        var (executor, _, _, windowA, windowB, windowC) = BuildThreeLeafRow();

        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.FocusRight), CancellationToken.None);

        Assert.Equal(1, windowB.TryActivateCallCount);
        Assert.Equal(0, windowA.TryActivateCallCount);
        Assert.Equal(0, windowC.TryActivateCallCount);
        Assert.Equal(0, windowA.SetPositionCallCount);
        Assert.Equal(0, windowB.SetPositionCallCount);
        Assert.Equal(0, windowC.SetPositionCallCount);
    }

    [Fact]
    public async Task ScheduleAsync_ToggleOrientation_FlipsParentAxis_AndRearranges()
    {
        var (executor, _, _, windowA, windowB, windowC) = BuildThreeLeafRow();
        executor.WorkArea = new Rect(0, 0, 900, 900);

        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.ToggleOrientation), CancellationToken.None);

        Assert.Equal(Rectangle.FromSize(0, 0, 900, 300), windowA.LastSetPosition);
        Assert.Equal(Rectangle.FromSize(0, 300, 900, 300), windowB.LastSetPosition);
        Assert.Equal(Rectangle.FromSize(0, 600, 900, 300), windowC.LastSetPosition);
    }

    [Fact]
    public async Task ScheduleAsync_ResizeRight_GrowsFocusedLeaf_TransfersFromNeighbor_AndConvertsRectToRectangle()
    {
        var leafA = new LeafNode(new WindowRef(new IntPtr(11)));
        var leafB = new LeafNode(new WindowRef(new IntPtr(12)));
        var group = new GroupNode(SplitAxis.Horizontal) { GroupLength = 900 };
        group.Children.Add(leafA);
        group.Children.Add(leafB);
        group.Sizes.Add(450);
        group.Sizes.Add(450);
        leafA.Parent = group;
        leafB.Parent = group;

        ITilingEngine engine = new LayoutTree(group);
        var registry = new WindowRegistry();
        var windowA = new RecordingWindow(leafA.Window.Handle, Rectangle.Empty);
        var windowB = new RecordingWindow(leafB.Window.Handle, Rectangle.Empty);
        registry.Register(windowA, leafA);
        registry.Register(windowB, leafB);
        var foreground = new FakeForegroundWindowSource { Handle = windowA.Handle };
        var executor = new ActionExecutor(engine, registry, foreground) { WorkArea = new Rect(0, 0, 900, 200) };

        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.ResizeRight), CancellationToken.None);

        Assert.Equal(Rectangle.FromSize(0, 0, 495, 200), windowA.LastSetPosition);
        Assert.Equal(Rectangle.FromSize(495, 0, 405, 200), windowB.LastSetPosition);
    }

    // V22-W1 (CRITICAL): TreeArranger.ArrangeAndPosition -- the shared choke point this call site
    // ALSO goes through -- now evicts a leaf whose SetPosition fails and reflows the survivors,
    // instead of leaving it stuck in the tree. Renamed/updated from the pre-fix
    // "CallsOnceAndDoesNotRetry" version, which pinned the OLD (broken) no-eviction behavior.
    [Fact]
    public async Task ScheduleAsync_ProtectedWindowFailsSetPosition_IsEvictedFromTree_SiblingsReflowIntoVacatedSpace()
    {
        var (executor, _, registry, windowA, windowB, windowC) = BuildThreeLeafRow();
        windowA.FailNextSetPosition();

        var exception = await Record.ExceptionAsync(
            () => executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.MoveRight), CancellationToken.None).AsTask());

        Assert.Null(exception);
        Assert.Equal(1, windowA.SetPositionCallCount); // exactly one failed attempt, never retried
        Assert.False(windowA.CanReposition);
        Assert.False(registry.TryGetLeaf(windowA.Handle, out _)); // untracked, like a WE-1 exclusion
        Assert.Equal(2, windowB.SetPositionCallCount); // initial slot + reflow into A's vacated space
        Assert.Equal(2, windowC.SetPositionCallCount); // initial slot + reflow into A's vacated space
        Assert.Equal(Rectangle.FromSize(0, 0, 450, 100), windowB.LastSetPosition);
        Assert.Equal(Rectangle.FromSize(450, 0, 450, 100), windowC.LastSetPosition);
    }

    [Theory]
    [InlineData(HotkeyActionKind.FocusIn)]
    [InlineData(HotkeyActionKind.FocusOut)]
    public async Task ScheduleAsync_FocusInOrFocusOut_NoOps_WithoutThrowing(HotkeyActionKind kind)
    {
        var (executor, _, _, windowA, windowB, windowC) = BuildThreeLeafRow();

        var exception = await Record.ExceptionAsync(
            () => executor.ScheduleAsync(new HotkeyAction(kind), CancellationToken.None).AsTask());

        Assert.Null(exception);
        Assert.Equal(0, windowA.SetPositionCallCount + windowB.SetPositionCallCount + windowC.SetPositionCallCount);
        Assert.Equal(0, windowA.TryActivateCallCount + windowB.TryActivateCallCount + windowC.TryActivateCallCount);
    }

    // WU18 (closes verify-report #21 V17-W1): reproduces probe P16's exact runtime shape -- two
    // windows on a SECONDARY (non-primary) monitor, one directional hotkey. Before the fix:
    // LayoutTree.MoveNode swapped the tree order but ActionExecutor always arranged the PRIMARY
    // tree/work area regardless of which tree it mutated, so zero SetPosition calls were issued
    // and neither window moved on screen -- exactly P16's "orderAfter=[502,501], aPos unchanged"
    // transcript. This proves the fix arranges the tree it actually mutated, on that tree's own
    // monitor work area.
    private static (
        ActionExecutor Executor,
        FakeForegroundWindowSource Foreground,
        WindowRegistry Registry,
        TreeManager TreeManager,
        RecordingWindow WindowA,
        RecordingWindow WindowB) BuildSecondaryMonitorPair()
    {
        var primary = new FakeDisplay(
            new IntPtr(1), Rectangle.FromSize(0, 0, 1920, 1080), Rectangle.FromSize(0, 0, 1920, 1080), 1.0, true);
        var secondary = new FakeDisplay(
            new IntPtr(2), Rectangle.FromSize(1920, 0, 1280, 720), Rectangle.FromSize(1920, 0, 1280, 720), 1.0, false);
        var registry = new WindowRegistry();
        var treeManager = new TreeManager(new IDisplay[] { primary, secondary }, primary, registry);
        treeManager.TryGetTree(secondary, out var secondaryTree);

        var windowA = new RecordingWindow(new IntPtr(501), Rectangle.FromSize(1920, 0, 640, 720));
        var windowB = new RecordingWindow(new IntPtr(502), Rectangle.FromSize(2560, 0, 640, 720));
        var leafA = new LeafNode(new WindowRef(windowA.Handle));
        var group = LayoutTree.AddChild(leafA, new WindowRef(windowB.Handle), 1280, 720);
        secondaryTree!.Root = group;
        var leafB = (LeafNode)group.Children[1];
        registry.Register(windowA, leafA);
        registry.Register(windowB, leafB);

        var foreground = new FakeForegroundWindowSource { Handle = windowB.Handle };
        ITilingEngine primaryEngine = new LayoutTree();
        var executor = new ActionExecutor(primaryEngine, registry, foreground)
        {
            WorkArea = new Rect(0, 0, 1920, 1080),
            TreeManager = treeManager,
        };

        return (executor, foreground, registry, treeManager, windowA, windowB);
    }

    [Fact]
    public async Task ScheduleAsync_MoveLeft_FocusOnSecondaryMonitor_ArrangesTheSecondaryTree_OnItsOwnWorkArea()
    {
        var (executor, _, _, _, windowA, windowB) = BuildSecondaryMonitorPair();

        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.MoveLeft), CancellationToken.None);

        Assert.Equal(1, windowA.SetPositionCallCount);
        Assert.Equal(1, windowB.SetPositionCallCount);
        Assert.Equal(Rectangle.FromSize(1920, 0, 640, 720), windowB.LastSetPosition);
        Assert.Equal(Rectangle.FromSize(2560, 0, 640, 720), windowA.LastSetPosition);
    }

    // WU18: the tree-order swap alone is not the user-visible property -- focus DIRECTION is.
    // After a Move swaps the secondary tree's order, FocusLeft must activate the window that is
    // NOW genuinely on screen to the left, not merely first in tree order.
    [Fact]
    public async Task ScheduleAsync_FocusLeft_AfterSecondaryMoveSwap_ActivatesTheWindowNowVisuallyOnTheLeft()
    {
        var (executor, foreground, _, _, windowA, windowB) = BuildSecondaryMonitorPair();
        foreground.Handle = windowA.Handle;
        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.MoveRight), CancellationToken.None);

        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.FocusLeft), CancellationToken.None);

        Assert.Equal(1, windowB.TryActivateCallCount);
        Assert.Equal(0, windowA.TryActivateCallCount);
        Assert.Equal(1920, windowB.LastSetPosition!.Value.Left);
    }

    private sealed class FakeForegroundWindowSource : IForegroundWindowSource
    {
        public nint Handle { get; set; }

        public nint GetForegroundHandle() => Handle;
    }
}
