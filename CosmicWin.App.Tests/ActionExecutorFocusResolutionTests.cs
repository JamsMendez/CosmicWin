using CosmicWin.App.Input;
using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;
using CosmicWin.Layout;

namespace CosmicWin.App.Tests;

/// <summary>
/// MR-2 root cause. The third supervised run's focus trace showed
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

    /// <summary>
    /// Two tiles under a real <see cref="TreeManager"/>, so the survivor search has a tree to name.
    /// </summary>
    private static (ActionExecutor Executor, FakeForegroundWindowSource Foreground, WindowRegistry Registry,
        LayoutTree Tree, LeafNode LeafA, RecordingWindow WindowA, RecordingWindow WindowB) BuildTwoTiles()
    {
        var leafA = new LeafNode(new WindowRef(new IntPtr(0xA1)));
        var leafB = new LeafNode(new WindowRef(new IntPtr(0xB2)));
        var group = new GroupNode(SplitAxis.Horizontal) { GroupLength = 1920 };
        foreach (var leaf in new[] { leafA, leafB })
        {
            group.Children.Add(leaf);
            group.Sizes.Add(960);
            leaf.Parent = group;
        }

        var registry = new WindowRegistry();
        var windowA = new RecordingWindow(leafA.Window.Handle, Rectangle.FromSize(0, 0, 960, 1080));
        var windowB = new RecordingWindow(leafB.Window.Handle, Rectangle.FromSize(960, 0, 960, 1080));
        registry.Register(windowA, leafA);
        registry.Register(windowB, leafB);

        var display = new FakeDisplay(
            new IntPtr(1), Rectangle.FromSize(0, 0, 1920, 1080), Rectangle.FromSize(0, 0, 1920, 1080), 1.0, true);
        var treeManager = new TreeManager([display], display, registry);
        treeManager.TryGetTree(display, out var managed);
        managed!.Root = new LayoutTree(group).Root;

        // Arranged, because the entry point measures TILES and a tree that has never been arranged
        // has none -- every leaf would be skipped and the fact would pass for the wrong reason.
        TreeArranger.ArrangeAndPosition(managed, registry, new Rect(0, 0, 1920, 1080), gap: 0, null);

        var foreground = new FakeForegroundWindowSource { Handle = windowA.Handle };
        var executor = new ActionExecutor(managed, registry, foreground)
        {
            WorkArea = new Rect(0, 0, 1920, 1080),
            TreeManager = treeManager,

            // Production reads the workspace, which tracks every top-level window rather than only
            // the tiled ones. Here the two doubles ARE every window there is.
            ResolveWindowBounds = handle =>
                handle == windowA.Handle ? windowA.Bounds
                : handle == windowB.Handle ? windowB.Bounds
                : null,
        };

        return (executor, foreground, registry, managed, leafA, windowA, windowB);
    }

    /// <summary>
    /// Untiles the focused window exactly as the adapter does when it parks one for its minimum
    /// size: out of the tree, out of the registry, still alive and still holding the foreground.
    /// </summary>
    private static void Park(LayoutTree tree, WindowRegistry registry, LeafNode leaf)
    {
        Assert.True(tree.Remove(leaf));
        Assert.True(registry.Remove(leaf.Window.Handle));
    }

    /// <summary>
    /// A window that gets PARKED while holding the focus must not take the keyboard with it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured on hardware, 19:18:45 onwards. NVIDIA Broadcast was tiled and focused, Alt+O made
    /// its tile shorter than its floor, and the adapter untiled it -- correctly. From that moment
    /// every layout chord died. The focus trace names it exactly:
    /// </para>
    /// <para>
    /// <c>focus Right foreground=0x400F4 focused=0x0 target=0x0 UnresolvedFocus activation=none</c>
    /// </para>
    /// <para>
    /// The existing fallback could not answer because the cache held the leaf of THAT window: the
    /// OS foreground is untracked (step one fails) and the cached leaf is the one just removed
    /// (step two fails). The fallback exists for a dialog stealing the foreground, where the tiled
    /// window behind it is still in the tree; here the window that left the tree is the one being
    /// looked at, and nothing was left to fall back to.
    /// </para>
    /// <para>
    /// This is the same SYMPTOM as the standing chord-dropout report reached by a completely
    /// different route, and it strands the user with no way back except the mouse.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ScheduleAsync_WhenTheFocusedWindowWasParked_EntersTheTreeAnyway()
    {
        var (executor, _, registry, tree, leafA, _, windowB) = BuildTwoTiles();

        // The cache is filled the ordinary way, by the window being focused while it is still tiled.
        Assert.Equal(leafA, executor.ResolveFocusedLeaf());
        Park(tree, registry, leafA);

        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.FocusRight), CancellationToken.None);

        Assert.Equal(1, windowB.TryActivateCallCount);
    }

    /// <summary>
    /// And the chord keeps working afterwards, from where it landed rather than from nowhere.
    /// </summary>
    /// <remarks>
    /// The entry point is a way back INTO the tree, not a special mode. Once the chord has put the
    /// user on a real tile the ordinary resolution owns every chord after it, which is only true if
    /// the entry advanced the cache like any other successful resolution.
    /// </remarks>
    [Fact]
    public async Task AfterEnteringTheTreeFromAParkedWindow_TheNextChordWalksFromWhereItLanded()
    {
        var (executor, _, registry, tree, leafA, windowA, windowB) = BuildTwoTiles();

        Assert.Equal(leafA, executor.ResolveFocusedLeaf());
        Park(tree, registry, leafA);

        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.FocusRight), CancellationToken.None);
        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.FocusRight), CancellationToken.None);

        // B is the only tile left, so a second walk from it has nowhere to go and activates nothing
        // new -- and it certainly never resurrects the parked window.
        Assert.Equal(1, windowB.TryActivateCallCount);
        Assert.Equal(0, windowA.TryActivateCallCount);
    }

    /// <summary>
    /// The way in still obeys the direction pressed: nothing that way is a real answer of "no".
    /// </summary>
    /// <remarks>
    /// Handing a direction chord whatever tile happened to survive is a MISDIRECTION, and this
    /// codebase already pins that with its own facts: a window evicted for refusing to be
    /// repositioned sits wherever it pinned itself, and FocusRight from it once activated a window
    /// physically to its LEFT. Being outside the tree does not suspend the meaning of the key.
    /// </remarks>
    [Fact]
    public async Task WhenNothingLiesInTheDirectionPressed_TheParkedWindowStaysWhereItIs()
    {
        var (executor, _, registry, tree, leafA, windowA, windowB) = BuildTwoTiles();

        // A holds the left tile, so there is nothing to ITS left once it is parked.
        Assert.Equal(leafA, executor.ResolveFocusedLeaf());
        Park(tree, registry, leafA);

        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.FocusLeft), CancellationToken.None);

        Assert.Equal(0, windowB.TryActivateCallCount);
        Assert.Equal(0, windowA.TryActivateCallCount);
    }

    /// <summary>
    /// The NEAREST tile that way, not merely one of them.
    /// </summary>
    /// <remarks>
    /// A direction names an order, not a set. Landing past the tile the user was pointing at is a
    /// jump they did not ask for, and with only two tiles on screen nothing can tell the two rules
    /// apart -- which is why this fact needs three.
    /// </remarks>
    [Fact]
    public async Task EnteringTheTree_LandsOnTheNearestTileThatWay_NotTheFurthest()
    {
        var leafA = new LeafNode(new WindowRef(new IntPtr(0xA1)));
        var leafB = new LeafNode(new WindowRef(new IntPtr(0xB2)));
        var leafC = new LeafNode(new WindowRef(new IntPtr(0xC3)));
        var group = new GroupNode(SplitAxis.Horizontal) { GroupLength = 1920 };
        foreach (var leaf in new[] { leafA, leafB, leafC })
        {
            group.Children.Add(leaf);
            group.Sizes.Add(640);
            leaf.Parent = group;
        }

        var registry = new WindowRegistry();
        var windowA = new RecordingWindow(leafA.Window.Handle, Rectangle.FromSize(0, 0, 640, 1080));
        var windowB = new RecordingWindow(leafB.Window.Handle, Rectangle.FromSize(640, 0, 640, 1080));
        var windowC = new RecordingWindow(leafC.Window.Handle, Rectangle.FromSize(1280, 0, 640, 1080));
        registry.Register(windowA, leafA);
        registry.Register(windowB, leafB);
        registry.Register(windowC, leafC);

        var display = new FakeDisplay(
            new IntPtr(1), Rectangle.FromSize(0, 0, 1920, 1080), Rectangle.FromSize(0, 0, 1920, 1080), 1.0, true);
        var treeManager = new TreeManager([display], display, registry);
        treeManager.TryGetTree(display, out var managed);
        managed!.Root = new LayoutTree(group).Root;
        TreeArranger.ArrangeAndPosition(managed, registry, new Rect(0, 0, 1920, 1080), gap: 0, null);

        var executor = new ActionExecutor(managed, registry, new FakeForegroundWindowSource { Handle = windowA.Handle })
        {
            WorkArea = new Rect(0, 0, 1920, 1080),
            TreeManager = treeManager,
            ResolveWindowBounds = handle =>
                handle == windowA.Handle ? windowA.Bounds
                : handle == windowB.Handle ? windowB.Bounds
                : handle == windowC.Handle ? windowC.Bounds
                : null,
        };

        Assert.Equal(leafA, executor.ResolveFocusedLeaf());
        Assert.True(managed.Remove(leafA));
        Assert.True(registry.Remove(leafA.Window.Handle));

        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.FocusRight), CancellationToken.None);

        Assert.Equal(1, windowB.TryActivateCallCount);
        Assert.Equal(0, windowC.TryActivateCallCount);
    }

    /// <summary>
    /// A tile the parked window is sitting exactly ON TOP OF is not in any direction from it.
    /// </summary>
    /// <remarks>
    /// A floating window can come to rest over a tile precisely -- and then every direction would
    /// answer with that same tile, so Left and Right would both land on the thing already under the
    /// user. "That way" has to mean strictly that way.
    /// </remarks>
    [Fact]
    public async Task ATileTheParkedWindowSitsExactlyOver_IsNotInAnyDirectionFromIt()
    {
        var (executor, _, registry, tree, leafA, windowA, windowB) = BuildTwoTiles();

        Assert.Equal(leafA, executor.ResolveFocusedLeaf());
        Park(tree, registry, leafA);

        // Parked, it drifts onto exactly the rectangle its neighbour holds.
        windowA.SimulateExternalMove(Rectangle.FromSize(960, 0, 960, 1080));

        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.FocusRight), CancellationToken.None);
        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.FocusLeft), CancellationToken.None);

        Assert.Equal(0, windowB.TryActivateCallCount);
    }

    /// <summary>
    /// The entry point is for CHORDS, and the focus border must not get it.
    /// </summary>
    /// <remarks>
    /// <c>ResolveFocusedLeaf</c> feeds the reflow callback that decides whether the border needs
    /// redrawing, and answering it with a tile the user is NOT on would let a stale border survive
    /// a reflow -- framing a window while a parked one holds the foreground. The border is drawn
    /// strictly from the real foreground for exactly that reason, and this keeps the reading it
    /// depends on honest.
    /// </remarks>
    [Fact]
    public void ResolveFocusedLeaf_WhenTheFocusedWindowWasParked_StillAnswersNothing()
    {
        var (executor, _, registry, tree, leafA, _, _) = BuildTwoTiles();

        Assert.Equal(leafA, executor.ResolveFocusedLeaf());
        Park(tree, registry, leafA);

        Assert.Null(executor.ResolveFocusedLeaf());
    }
}
