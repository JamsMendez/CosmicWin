using CosmicWin.App.Input;
using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;
using CosmicWin.Layout;

namespace CosmicWin.App.Tests;

/// <summary>
/// Alt+Q asks the window the user is looking at to close.
/// </summary>
/// <remarks>
/// Answered from the OS foreground rather than the tracked leaf, exactly like the desktop chords:
/// closing a window CosmicWin does not tile is an ordinary thing to want, and the registry holds
/// tiled leaves only.
/// </remarks>
public class ActionExecutorCloseTests
{
    [Fact]
    public async Task CloseWindow_AsksTheForegroundWindow()
    {
        var h = Build();

        await h.Executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.CloseWindow), CancellationToken.None);

        Assert.Equal([h.Tracked.Handle], h.Asked);
    }

    /// <summary>A window the tree never held is still the window the user is looking at.</summary>
    [Fact]
    public async Task CloseWindow_ForegroundIsUntracked_AsksItAnyway()
    {
        var h = Build();
        var untracked = new IntPtr(0xBEEF);
        h.Foreground.Handle = untracked;

        await h.Executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.CloseWindow), CancellationToken.None);

        Assert.Equal([untracked], h.Asked);
    }

    /// <summary>
    /// The tree is NOT touched. WM_CLOSE is a request an application may refuse -- an unsaved
    /// document puts up its own dialog and stays exactly where it is -- so removing the leaf here
    /// would desync the layout from the screen on every refusal, with no event to put it back. The
    /// window actually leaving arrives on its own, through the destroy path that already reflows.
    /// </summary>
    [Fact]
    public async Task CloseWindow_LeavesTheTreeAlone_BecauseTheWindowMayRefuse()
    {
        var h = Build();
        var before = Assert.IsType<GroupNode>(h.Tree.Root).Children.Count;

        await h.Executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.CloseWindow), CancellationToken.None);

        Assert.Equal(before, Assert.IsType<GroupNode>(h.Tree.Root).Children.Count);
        Assert.Equal(0, h.Tracked.SetPositionCallCount);
    }

    /// <summary>Nothing focused is nothing to close, and must not reach for a remembered leaf.</summary>
    [Fact]
    public async Task CloseWindow_NoForegroundWindow_AsksNothing()
    {
        var h = Build();
        h.Foreground.Handle = 0;

        await h.Executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.CloseWindow), CancellationToken.None);

        Assert.Empty(h.Asked);
    }

    private sealed record Harness(
        ActionExecutor Executor,
        LayoutTree Tree,
        RecordingWindow Tracked,
        FakeForeground Foreground,
        List<nint> Asked);

    private static Harness Build()
    {
        var registry = new WindowRegistry();
        var group = new GroupNode(SplitAxis.Horizontal) { GroupLength = 1920 };
        var first = new LeafNode(new WindowRef(new IntPtr(0xC1))) { Parent = group };
        var second = new LeafNode(new WindowRef(new IntPtr(0xC2))) { Parent = group };
        group.Children.AddRange([first, second]);
        group.Sizes.AddRange([960, 960]);

        var tracked = new RecordingWindow(first.Window.Handle, Rectangle.FromSize(0, 0, 960, 1080));
        registry.Register(tracked, first);
        registry.Register(
            new RecordingWindow(second.Window.Handle, Rectangle.FromSize(960, 0, 960, 1080)), second);

        var tree = new LayoutTree(group);
        var foreground = new FakeForeground { Handle = tracked.Handle };
        var asked = new List<nint>();

        var executor = new ActionExecutor(tree, registry, foreground)
        {
            WorkArea = new Rect(0, 0, 1920, 1080),
            CloseWindowAt = handle =>
            {
                asked.Add(handle);
                return true;
            },
        };

        return new Harness(executor, tree, tracked, foreground, asked);
    }

    private sealed class FakeForeground : IForegroundWindowSource
    {
        public nint Handle { get; set; }

        public nint GetForegroundHandle() => Handle;
    }
}
