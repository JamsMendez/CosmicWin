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

    [Fact]
    public async Task ScheduleAsync_ProtectedWindowFailsSetPosition_CallsOnceAndDoesNotRetry_OtherLeavesStillPositioned()
    {
        var (executor, _, _, windowA, windowB, windowC) = BuildThreeLeafRow();
        windowA.FailNextSetPosition();

        var exception = await Record.ExceptionAsync(
            () => executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.MoveRight), CancellationToken.None).AsTask());

        Assert.Null(exception);
        Assert.Equal(1, windowA.SetPositionCallCount);
        Assert.False(windowA.CanReposition);
        Assert.Equal(1, windowB.SetPositionCallCount);
        Assert.Equal(1, windowC.SetPositionCallCount);
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

    private sealed class FakeForegroundWindowSource : IForegroundWindowSource
    {
        public nint Handle { get; set; }

        public nint GetForegroundHandle() => Handle;
    }
}
