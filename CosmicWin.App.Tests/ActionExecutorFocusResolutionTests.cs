using CosmicWin.App.Input;
using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;
using CosmicWin.Layout;

namespace CosmicWin.App.Tests;

/// <summary>
/// MR-2 root cause (Engram discovery #104). The third supervised run's focus trace showed
/// activation to `0x99030A` FAILING at 12:46:32 and the very next chord ten seconds later reporting
/// `focused=0x99030A` anyway: <c>ActionExecutor</c> consulted its <c>_focused</c> cache FIRST and
/// returned it on nothing more than "still tracked and alive", and <c>MoveFocus</c> advanced that
/// cache BEFORE knowing whether activation worked. One failed activation therefore desynced
/// CosmicWin's focus model from the desktop permanently, and because
/// <c>Win32NativeWindowSource.TryActivateWindow</c> short-circuits on <c>foreground == target</c>,
/// the drifted model then "activated" the window the user was already on -- reporting success while
/// nothing moved on screen. These facts pin both halves of the fix: the OS foreground wins whenever
/// it maps to a tracked leaf, and the cache only advances on a real activation.
/// </summary>
public sealed class ActionExecutorFocusResolutionTests
{
    private static (ActionExecutor Executor, FakeForegroundWindowSource Foreground, WindowRegistry Registry,
        RecordingWindow WindowA, RecordingWindow WindowB, RecordingWindow WindowC) BuildThreeLeafRow()
    {
        var leafA = new LeafNode(new WindowRef(new IntPtr(1)));
        var leafB = new LeafNode(new WindowRef(new IntPtr(2)));
        var leafC = new LeafNode(new WindowRef(new IntPtr(3)));
        var group = new GroupNode(SplitAxis.Horizontal) { GroupLength = 900 };
        foreach (var leaf in new[] { leafA, leafB, leafC })
        {
            group.Children.Add(leaf);
            group.Sizes.Add(300);
            leaf.Parent = group;
        }

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

    /// <summary>
    /// The desync-killer. After a focus move leaves the cache on B, the user clicks C by hand -- the
    /// OS foreground is now C and the cache is stale. `FocusLeft` must walk from C (activating B
    /// again), NOT from the stale cache B (which would activate A).
    /// </summary>
    [Fact]
    public async Task ScheduleAsync_WhenTheOsForegroundDisagreesWithTheCache_WalksFromTheOsForeground()
    {
        var (executor, foreground, _, windowA, windowB, windowC) = BuildThreeLeafRow();
        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.FocusRight), CancellationToken.None);
        foreground.Handle = windowC.Handle;

        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.FocusLeft), CancellationToken.None);

        Assert.Equal(0, windowA.TryActivateCallCount);
        Assert.Equal(2, windowB.TryActivateCallCount);
    }

    /// <summary>
    /// The cache is not deleted, only demoted: when the real foreground is a window CosmicWin does
    /// not track (a dialog, a non-tiled app), the last known leaf still drives the chord instead of
    /// the whole action becoming a no-op.
    /// </summary>
    [Fact]
    public async Task ScheduleAsync_WhenTheOsForegroundIsUntracked_FallsBackToTheCachedLeaf()
    {
        var (executor, foreground, _, _, _, windowC) = BuildThreeLeafRow();
        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.FocusRight), CancellationToken.None);
        foreground.Handle = new IntPtr(404);

        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.FocusRight), CancellationToken.None);

        Assert.Equal(1, windowC.TryActivateCallCount);
    }

    /// <summary>
    /// The exact 12:46:32 shape from the trace: activation FAILS, so the cache must stay where the
    /// user actually is. Driven through the untracked-foreground fallback so the assertion reads the
    /// cache itself rather than the foreground lookup that would mask it.
    /// </summary>
    [Fact]
    public async Task ScheduleAsync_WhenActivationFails_DoesNotAdvanceTheCachedFocus()
    {
        var (executor, foreground, _, _, windowB, windowC) = BuildThreeLeafRow();
        windowB.FailNextActivate();
        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.FocusRight), CancellationToken.None);
        foreground.Handle = new IntPtr(404);

        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.FocusRight), CancellationToken.None);

        Assert.Equal(2, windowB.TryActivateCallCount);
        Assert.Equal(0, windowC.TryActivateCallCount);
    }

    /// <summary>
    /// A target leaf whose window is no longer registered must not advance the cache either -- same
    /// reasoning as a failed activation, and it keeps <c>UntrackedTarget</c> from becoming a second
    /// desync route. The final untracked-foreground chord forces the assertion to read the cache: if
    /// it had wrongly advanced to B, this last <c>FocusRight</c> would reach C.
    /// </summary>
    [Fact]
    public async Task ScheduleAsync_WhenTheTargetWindowIsUntracked_DoesNotAdvanceTheCachedFocus()
    {
        var (executor, foreground, registry, windowA, windowB, windowC) = BuildThreeLeafRow();
        registry.Remove(windowB.Handle);
        foreground.Handle = windowA.Handle;

        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.FocusRight), CancellationToken.None);
        foreground.Handle = new IntPtr(404);
        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.FocusRight), CancellationToken.None);

        Assert.Equal(0, windowB.TryActivateCallCount);
        Assert.Equal(0, windowC.TryActivateCallCount);
    }

    private sealed class FakeForegroundWindowSource : IForegroundWindowSource
    {
        public nint Handle { get; set; }

        public nint GetForegroundHandle() => Handle;
    }
}
