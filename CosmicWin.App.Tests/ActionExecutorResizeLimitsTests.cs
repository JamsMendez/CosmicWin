using CosmicWin.App.Input;
using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;
using CosmicWin.Layout;

namespace CosmicWin.App.Tests;

/// <summary>
/// The resize chord stops where the WINDOW stops, not only where the layout's ratio does.
/// </summary>
/// <remarks>
/// <para>
/// Measured with NVIDIA Broadcast, which will not go under 772 tall and will not go over 1000.
/// Shrinking past the floor was a tug of war -- the chord took the slot to 703, the adapter gave the
/// space straight back to reach 772, and five presses produced five rounds and no movement.
/// </para>
/// <para>
/// Growing past the ceiling was worse, because nothing corrected it: the slot went to 1117, the
/// window stayed at 1000, and the neighbour was squashed to 258 in exchange for dead space.
/// </para>
/// </remarks>
public sealed class ActionExecutorResizeLimitsTests
{
    private static (ActionExecutor Executor, GroupNode Group, RecordingWindow Constrained)
        StackedPair(int first, int second)
    {
        var topLeaf = new LeafNode(new WindowRef(new IntPtr(1)));
        var bottomLeaf = new LeafNode(new WindowRef(new IntPtr(2)));
        var group = new GroupNode(SplitAxis.Vertical) { GroupLength = 1376 };
        LayoutTree.AddChild(group, topLeaf, index: 0);
        LayoutTree.AddChild(group, bottomLeaf, index: 1);
        group.Sizes[0] = first;
        group.Sizes[1] = second;

        var registry = new WindowRegistry();
        var top = new RecordingWindow(topLeaf.Window.Handle, Rectangle.FromSize(0, 0, 1708, first));
        var bottom = new RecordingWindow(bottomLeaf.Window.Handle, Rectangle.FromSize(0, first, 1708, second));
        registry.Register(top, topLeaf);
        registry.Register(bottom, bottomLeaf);

        var executor = new ActionExecutor(
            new LayoutTree(group), registry, new FakeForegroundWindowSource { Handle = bottom.Handle })
        {
            WorkArea = new Rect(0, 0, 1708, 1376),
        };

        return (executor, group, bottom);
    }

    private sealed class FakeForegroundWindowSource : IForegroundWindowSource
    {
        public nint Handle { get; set; }

        public nint GetForegroundHandle() => Handle;
    }

    [Fact]
    public async Task Resize_ShrinkingAWindowWithAFloor_StopsOnIt()
    {
        var (executor, group, constrained) = StackedPair(first: 604, second: 772);
        executor.ResolveSizeLimits = handle =>
            handle == constrained.Handle ? (0, 740, 0, int.MaxValue) : (0, 0, 0, 0);

        // Down with no neighbour below shrinks it; the default step alone would take 69.
        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.ResizeDown), CancellationToken.None);

        Assert.Equal(740 + TreeArranger.Gap, group.Sizes[1]);
    }

    [Fact]
    public async Task Resize_GrowingAWindowWithACeiling_StopsOnIt()
    {
        var (executor, group, constrained) = StackedPair(first: 604, second: 772);
        executor.ResolveSizeLimits = handle =>
            handle == constrained.Handle ? (0, 0, int.MaxValue, 800) : (0, 0, int.MaxValue, int.MaxValue);

        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.ResizeUp), CancellationToken.None);

        Assert.Equal(800 + TreeArranger.Gap, group.Sizes[1]);
    }

    /// <summary>
    /// The limits are read for the axis being resized, so a floor on the OTHER one does not pin a
    /// resize that has nothing to do with it.
    /// </summary>
    [Fact]
    public async Task Resize_ReadsTheLimitForTheAxisItIsMoving()
    {
        var (executor, group, constrained) = StackedPair(first: 604, second: 772);

        // A width floor of 5000, on a group that divides HEIGHT. It must be ignored entirely.
        executor.ResolveSizeLimits = handle =>
            handle == constrained.Handle ? (5000, 0, int.MaxValue, int.MaxValue) : (0, 0, int.MaxValue, int.MaxValue);

        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.ResizeDown), CancellationToken.None);

        // The default step is five per cent of the group: 1376 x 0.05 rounds to 69, which is the
        // 703 the hardware trace showed before any of this existed.
        Assert.Equal(703, group.Sizes[1]);
    }

    /// <summary>
    /// Unwired, as in every test and every build that predates it, a resize behaves exactly as it
    /// always did.
    /// </summary>
    [Fact]
    public async Task Resize_WithNoLimitsResolver_IsUnchanged()
    {
        var (executor, group, _) = StackedPair(first: 604, second: 772);

        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.ResizeDown), CancellationToken.None);

        // The default step is five per cent of the group: 1376 x 0.05 rounds to 69, which is the
        // 703 the hardware trace showed before any of this existed.
        Assert.Equal(703, group.Sizes[1]);
    }
}
