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
            handle == constrained.Handle ? (0, 740, int.MaxValue, int.MaxValue) : (0, 0, int.MaxValue, int.MaxValue);

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
    /// The NEIGHBOUR's floor stops the chord as surely as the focused window's own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reported from real use, and the half that was missing: growing Alacritty upward pushed NVIDIA
    /// Broadcast's slot to 703 and then 634 while the chord stayed inside its own limits the whole
    /// time -- it had none -- and the adapter had to hand the space back afterwards.
    /// </para>
    /// <para>
    /// Bounding only the window that moves moves the problem instead of solving it. A window is
    /// invaded by its neighbour's resize exactly as easily as by its own.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Resize_GrowingIntoANeighbourWithAFloor_StopsOnTheNeighboursFloor()
    {
        var (executor, group, constrained) = StackedPair(first: 604, second: 772);

        // The focused window is the one at index 1 and has no limits at all; the TOP one will not
        // go under 560.
        var top = group.Children[0] as LeafNode;
        executor.ResolveSizeLimits = handle =>
            handle == top!.Window.Handle ? (0, 560, int.MaxValue, int.MaxValue) : (0, 0, int.MaxValue, int.MaxValue);
        _ = constrained;

        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.ResizeUp), CancellationToken.None);

        Assert.Equal(560 + TreeArranger.Gap, group.Sizes[0]);
    }

    /// <summary>And a neighbour already on its floor gives nothing at all.</summary>
    [Fact]
    public async Task Resize_GrowingIntoANeighbourAlreadyOnItsFloor_ChangesNothing()
    {
        var (executor, group, _) = StackedPair(first: 604, second: 772);

        var top = group.Children[0] as LeafNode;
        executor.ResolveSizeLimits = handle =>
            handle == top!.Window.Handle ? (0, 604, int.MaxValue, int.MaxValue) : (0, 0, int.MaxValue, int.MaxValue);

        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.ResizeUp), CancellationToken.None);

        Assert.Equal([604, 772], group.Sizes);
    }

    /// <summary>
    /// Shrinking a window may not hand its neighbour a slot the neighbour cannot fill.
    /// </summary>
    /// <remarks>
    /// The mirror of the invasion, and it slipped through the first version of this. Bounding only
    /// the FOCUSED node's ceiling let the opposite happen on hardware: shrinking Alacritty handed
    /// NVIDIA Broadcast a slot 110 pixels taller than the 1000 it will ever use -- dead space bought
    /// with a real window's room. Naming the two sides by ROLE rather than by focus bounds both
    /// with one rule.
    /// </remarks>
    [Fact]
    public async Task Resize_ShrinkingIntoANeighbourWithACeiling_StopsOnTheNeighboursCeiling()
    {
        var (executor, group, _) = StackedPair(first: 604, second: 772);

        var top = group.Children[0] as LeafNode;
        executor.ResolveSizeLimits = handle =>
            handle == top!.Window.Handle ? (0, 0, int.MaxValue, 650) : (0, 0, int.MaxValue, int.MaxValue);

        // Down with no neighbour below shrinks the focused node and hands the space upward.
        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.ResizeDown), CancellationToken.None);

        Assert.Equal(650 + TreeArranger.Gap, group.Sizes[0]);
    }

    /// <summary>
    /// A branch answers for the windows inside it, because a constrained window is usually NESTED
    /// rather than a direct sibling of whoever is being resized.
    /// </summary>
    /// <remarks>
    /// Which is the reported shape exactly: NVIDIA Broadcast shared a column with another window,
    /// and what sat beside the window being grown was the whole branch, not Broadcast. A resize that
    /// only asked about direct siblings would sail straight past it.
    /// </remarks>
    [Fact]
    public async Task Resize_GrowingIntoABranch_StopsOnTheFloorOfTheWindowInsideIt()
    {
        var buried = new LeafNode(new WindowRef(new IntPtr(10)));
        var alongside = new LeafNode(new WindowRef(new IntPtr(20)));
        var branch = new GroupNode(SplitAxis.Vertical) { GroupLength = 1376 };
        LayoutTree.AddChild(branch, buried, index: 0);
        LayoutTree.AddChild(branch, alongside, index: 1);

        var grower = new LeafNode(new WindowRef(new IntPtr(30)));
        var root = new GroupNode(SplitAxis.Horizontal) { GroupLength = 3424 };
        LayoutTree.AddChild(root, branch, index: 0);
        LayoutTree.AddChild(root, grower, index: 1);
        root.Sizes[0] = 1712;
        root.Sizes[1] = 1712;

        var registry = new WindowRegistry();
        var buriedWindow = new RecordingWindow(buried.Window.Handle, Rectangle.FromSize(0, 0, 1712, 688));
        var alongsideWindow = new RecordingWindow(alongside.Window.Handle, Rectangle.FromSize(0, 688, 1712, 688));
        var growerWindow = new RecordingWindow(grower.Window.Handle, Rectangle.FromSize(1712, 0, 1712, 1376));
        registry.Register(buriedWindow, buried);
        registry.Register(alongsideWindow, alongside);
        registry.Register(growerWindow, grower);

        var executor = new ActionExecutor(
            new LayoutTree(root), registry, new FakeForegroundWindowSource { Handle = growerWindow.Handle })
        {
            WorkArea = new Rect(0, 0, 3424, 1376),

            // The buried window will not go under 1600 WIDE; the one beside it will not go under
            // 900. Across the branch's own axis the two overlap in width, so the LARGER wins --
            // 1600, not 2500.
            ResolveSizeLimits = handle =>
                handle == buriedWindow.Handle ? (1600, 0, int.MaxValue, int.MaxValue)
                : handle == alongsideWindow.Handle ? (900, 0, int.MaxValue, int.MaxValue)
                : (0, 0, int.MaxValue, int.MaxValue),
        };

        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.ResizeLeft), CancellationToken.None);

        Assert.Equal(1600 + TreeArranger.Gap, root.Sizes[0]);
    }

    /// <summary>
    /// Along a branch's OWN axis its windows sit end to end, so their floors add up rather than
    /// overlapping.
    /// </summary>
    [Fact]
    public async Task Resize_GrowingIntoABranchAlongItsOwnAxis_AddsTheFloorsUp()
    {
        var buried = new LeafNode(new WindowRef(new IntPtr(10)));
        var alongside = new LeafNode(new WindowRef(new IntPtr(20)));
        var branch = new GroupNode(SplitAxis.Vertical) { GroupLength = 900 };
        LayoutTree.AddChild(branch, buried, index: 0);
        LayoutTree.AddChild(branch, alongside, index: 1);

        var grower = new LeafNode(new WindowRef(new IntPtr(30)));
        var root = new GroupNode(SplitAxis.Vertical) { GroupLength = 1376 };
        LayoutTree.AddChild(root, branch, index: 0);
        LayoutTree.AddChild(root, grower, index: 1);
        // Close enough to the branch's floor that one step of five per cent -- 69 of 1376 -- would
        // overshoot it, which is what makes the clamp the thing being measured.
        root.Sizes[0] = 850;
        root.Sizes[1] = 526;

        var registry = new WindowRegistry();
        var buriedWindow = new RecordingWindow(buried.Window.Handle, Rectangle.FromSize(0, 0, 1712, 450));
        var alongsideWindow = new RecordingWindow(alongside.Window.Handle, Rectangle.FromSize(0, 450, 1712, 450));
        var growerWindow = new RecordingWindow(grower.Window.Handle, Rectangle.FromSize(0, 900, 1712, 476));
        registry.Register(buriedWindow, buried);
        registry.Register(alongsideWindow, alongside);
        registry.Register(growerWindow, grower);

        var executor = new ActionExecutor(
            new LayoutTree(root), registry, new FakeForegroundWindowSource { Handle = growerWindow.Handle })
        {
            WorkArea = new Rect(0, 0, 1712, 1376),

            // Stacked inside the branch, so 500 and 300 tall come to 800 -- not 500.
            ResolveSizeLimits = handle =>
                handle == buriedWindow.Handle ? (0, 500, int.MaxValue, int.MaxValue)
                : handle == alongsideWindow.Handle ? (0, 300, int.MaxValue, int.MaxValue)
                : (0, 0, int.MaxValue, int.MaxValue),
        };

        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.ResizeUp), CancellationToken.None);

        Assert.Equal(800 + (2 * TreeArranger.Gap), root.Sizes[0]);
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
