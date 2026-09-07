using CosmicWin.App.Input;
using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;
using CosmicWin.Layout;

namespace CosmicWin.App.Tests;

/// <summary>
/// What a chord does once the tray's tiling switch is off.
/// </summary>
/// <remarks>
/// The line this draws is the whole feature: a chord that acts on the TREE is dropped, and a chord
/// about which desktop the user is looking at -- or about the window in front of them -- is
/// answered exactly as before. Pausing already stops all of them; this exists because stopping all
/// of them is not what was asked for.
/// </remarks>
public sealed class ActionExecutorTilingDisabledTests
{
    private sealed class FakeVirtualDesktops : IVirtualDesktopService
    {
        public bool IsSupported => true;

        public int Count => 2;

        public int CurrentIndex => 1;

        public string? LastError => null;

        public Guid CurrentDesktopId => Guid.Empty;

        public List<int> Switched { get; } = [];

        public List<(nint Handle, int Index)> Moved { get; } = [];

        public bool TrySwitchTo(int oneBasedIndex)
        {
            Switched.Add(oneBasedIndex);
            return true;
        }

        public bool TryMoveWindowTo(nint windowHandle, int oneBasedIndex)
        {
            Moved.Add((windowHandle, oneBasedIndex));
            return true;
        }
    }

    private sealed class FakeForegroundWindowSource : IForegroundWindowSource
    {
        public nint Handle { get; set; }

        public nint GetForegroundHandle() => Handle;
    }

    private sealed record Harness(
        ActionExecutor Executor, FakeVirtualDesktops Desktops,
        RecordingWindow WindowA, RecordingWindow WindowB, List<nint> Closed);

    /// <summary>Two leaves side by side, with the left one focused and tiling switched OFF.</summary>
    private static Harness BuildWithTilingOff()
    {
        var leafA = new LeafNode(new WindowRef(new IntPtr(1)));
        var leafB = new LeafNode(new WindowRef(new IntPtr(2)));
        var group = new GroupNode(SplitAxis.Horizontal) { GroupLength = 800 };
        group.Children.Add(leafA);
        group.Children.Add(leafB);
        group.Sizes.Add(400);
        group.Sizes.Add(400);
        leafA.Parent = group;
        leafB.Parent = group;

        var registry = new WindowRegistry();
        var windowA = new RecordingWindow(leafA.Window.Handle, Rectangle.Empty);
        var windowB = new RecordingWindow(leafB.Window.Handle, Rectangle.Empty);
        registry.Register(windowA, leafA);
        registry.Register(windowB, leafB);

        var desktops = new FakeVirtualDesktops();
        var closed = new List<nint>();
        var executor = new ActionExecutor(
            new LayoutTree(group), registry, new FakeForegroundWindowSource { Handle = windowA.Handle })
        {
            WorkArea = new Rect(0, 0, 800, 600),
            VirtualDesktops = desktops,
            CloseWindowAt = handle =>
            {
                closed.Add(handle);
                return true;
            },
            TilingEnabled = () => false,
        };

        return new Harness(executor, desktops, windowA, windowB, closed);
    }

    /// <summary>
    /// Every chord that reaches the tree, in one fact. Dropped rather than half-answered: with no
    /// layout in force there is nothing for a focus walk to walk and nothing for a move to move.
    /// </summary>
    [Theory]
    [InlineData(HotkeyActionKind.FocusRight)]
    [InlineData(HotkeyActionKind.FocusLeft)]
    [InlineData(HotkeyActionKind.MoveRight)]
    [InlineData(HotkeyActionKind.MoveLeft)]
    [InlineData(HotkeyActionKind.ResizeRight)]
    [InlineData(HotkeyActionKind.ToggleOrientation)]
    [InlineData(HotkeyActionKind.FocusIn)]
    [InlineData(HotkeyActionKind.FocusOut)]
    public async Task WithTilingOff_ALayoutChordMovesAndActivatesNothing(HotkeyActionKind kind)
    {
        var harness = BuildWithTilingOff();

        await harness.Executor.ScheduleAsync(new HotkeyAction(kind), CancellationToken.None);

        Assert.Equal(0, harness.WindowA.SetPositionCallCount);
        Assert.Equal(0, harness.WindowB.SetPositionCallCount);
        Assert.Equal(0, harness.WindowB.TryActivateCallCount);
    }

    /// <summary>The half the user asked to keep: walking between desktops.</summary>
    [Fact]
    public async Task WithTilingOff_TheDesktopSwitchChordStillReachesTheShell()
    {
        var harness = BuildWithTilingOff();

        await harness.Executor.ScheduleAsync(
            new HotkeyAction(HotkeyActionKind.SwitchDesktop, 2), CancellationToken.None);

        Assert.Equal([2], harness.Desktops.Switched);
    }

    /// <summary>And the other half: sending the window in front of you to another desktop.</summary>
    [Fact]
    public async Task WithTilingOff_TheSendWindowToDesktopChordStillReachesTheShell()
    {
        var harness = BuildWithTilingOff();

        await harness.Executor.ScheduleAsync(
            new HotkeyAction(HotkeyActionKind.MoveWindowToDesktop, 3), CancellationToken.None);

        Assert.Equal([(harness.WindowA.Handle, 3)], harness.Desktops.Moved);
    }

    /// <summary>
    /// Closing the window in front of you is not a layout operation either -- it asks an
    /// application to close and never touches the tree -- so it keeps working like the desktop
    /// chords beside it.
    /// </summary>
    [Fact]
    public async Task WithTilingOff_ClosingTheForegroundWindowStillWorks()
    {
        var harness = BuildWithTilingOff();

        await harness.Executor.ScheduleAsync(
            new HotkeyAction(HotkeyActionKind.CloseWindow), CancellationToken.None);

        Assert.Equal([harness.WindowA.Handle], harness.Closed);
    }

    /// <summary>
    /// Unset, the gate is open. Every test and call site that predates the switch must behave
    /// exactly as it did, which is what makes this a feature rather than a rewrite.
    /// </summary>
    [Fact]
    public async Task WithNoGateWired_TheLayoutChordsStillWork()
    {
        var harness = BuildWithTilingOff();
        harness.Executor.TilingEnabled = () => true;

        await harness.Executor.ScheduleAsync(
            new HotkeyAction(HotkeyActionKind.MoveRight), CancellationToken.None);

        Assert.Equal(1, harness.WindowA.SetPositionCallCount);
    }

}
