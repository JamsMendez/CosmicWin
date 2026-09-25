using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App.Tests;

/// <summary>
/// A window that goes fullscreen keeps its slot in the tree, and lands back on its tile when it
/// stops being fullscreen.
/// </summary>
/// <remarks>
/// <para>
/// Measured on hardware with Chrome and Brave in tiling mode. A fullscreen window (F11, or a video's
/// fullscreen button) sits on the whole monitor, which is deliberately NOT its tile. The reconcile
/// poll reported that as a bounds change every two seconds, the adapter re-arranged, Chrome snapped
/// straight back to fullscreen, and after twelve such misses the fighting-window guard evicted the
/// handle and refused it for life. Leaving fullscreen then brought back a window that was no longer
/// in the tree, and only closing and reopening it fixed that.
/// </para>
/// <para>
/// The traced shape: <c>reflow [L=0 T=0 W=3440 H=1440] -&gt; [L=1724 T=8 W=1708 H=1376]</c> twelve
/// times, then <c>gave up ... never reached its tile in 12 attempts</c>.
/// </para>
/// <para>
/// What a fullscreen Chrome looks like from outside, measured: <c>WS_CAPTION</c> and
/// <c>WS_THICKFRAME</c> cleared, <c>WS_MAXIMIZE</c> NOT set, bounds on the whole monitor -- and
/// sometimes one pixel short of it. A genuinely MAXIMISED window on a monitor with an auto-hide
/// taskbar covers the monitor as well but keeps its caption and <c>WS_MAXIMIZE</c>, so it must not
/// be mistaken for this.
/// </para>
/// </remarks>
public sealed class FullscreenWindowTests
{
    /// <summary>A 1920x1080 monitor whose taskbar takes the bottom 48, so a tile is never the monitor.</summary>
    private static readonly Rectangle Monitor = Rectangle.FromSize(0, 0, 1920, 1080);

    private static readonly Rectangle WorkArea = Rectangle.FromSize(0, 0, 1920, 1032);

    /// <summary>What a Chrome window carries when it is tiled: a caption and a resize border.</summary>
    private const uint CaptionedStyle =
        0x00080000u | 0x00010000u | 0x00020000u | WindowStyleFlags.Caption | WindowStyleFlags.ThickFrame;

    /// <summary>Far above the fighter limit, so "still tracked" cannot be a slow verdict.</summary>
    private const int EnoughRounds = 30;

    private sealed class RecordingTrace : CosmicWin.App.Diagnostics.IDesktopTrace
    {
        public List<string> Lines { get; } = [];

        public void Record(string line) => Lines.Add(line);
    }

    /// <summary>One monitor, a settled neighbour and a Chrome window tiled beside it.</summary>
    private sealed class Harness : IDisposable
    {
        public Harness()
        {
            var primary = new FakeDisplay(new IntPtr(1), Monitor, WorkArea, 1.0, true);
            Primary = primary;
            Registry = new WindowRegistry();
            Trees = new TreeManager([primary], primary, Registry);
            Workspace = new FakeWorkspace();
            Adapter = new MultiMonitorWorkspaceAdapter(
                Workspace, Trees, Registry, () => ExceptionList.Empty, () => false, () => null)
            {
                Trace = Trace,
            };

            Neighbour = new RecordingWindow(new IntPtr(10), Rectangle.FromSize(0, 0, 400, 300), style: CaptionedStyle);
            Chrome = new RecordingWindow(
                new IntPtr(20), Rectangle.FromSize(0, 0, 400, 300),
                className: "Chrome_WidgetWin_1", processName: "chrome.exe", style: CaptionedStyle);
            Workspace.RaiseWindowAdded(Neighbour);
            Workspace.RaiseWindowAdded(Chrome);

            Tile = Chrome.Bounds;
        }

        public IDisplay Primary { get; }

        public WindowRegistry Registry { get; }

        public TreeManager Trees { get; }

        public FakeWorkspace Workspace { get; }

        public MultiMonitorWorkspaceAdapter Adapter { get; }

        public RecordingTrace Trace { get; } = new();

        public RecordingWindow Neighbour { get; }

        public RecordingWindow Chrome { get; }

        /// <summary>Where the tree put Chrome, which is nowhere near the whole monitor.</summary>
        public Rectangle Tile { get; }

        public bool ChromeInTree =>
            Trees.LeavesOn(Primary).Any(leaf => leaf.Window.Handle == Chrome.Handle) &&
            Registry.TryGetLeaf(Chrome.Handle, out _);

        /// <summary>
        /// The fullscreen window as Chrome really behaves: it takes the monitor, and any attempt to
        /// put it back on a tile is undone at once.
        /// </summary>
        public void GoFullscreen(Rectangle covering)
        {
            Chrome.SimulateFullscreen(covering);
            Chrome.SnapsBackTo = covering;
        }

        public void Poll(int rounds)
        {
            for (var round = 0; round < rounds; round++)
            {
                Workspace.RaiseWindowBoundsChanged(Chrome);
            }
        }

        public int FullscreenLines => Trace.Lines.Count(line => line.StartsWith("fullscreen hwnd=0x14 ", StringComparison.Ordinal));

        public void Dispose() => Adapter.Dispose();
    }

    [Fact]
    public void AWindowThatGoesFullscreen_IsNotEvictedHoweverLongItStaysThere()
    {
        using var h = new Harness();

        h.GoFullscreen(Monitor);
        h.Poll(EnoughRounds);

        Assert.True(h.ChromeInTree);
        Assert.DoesNotContain(h.Trace.Lines, line => line.Contains("gave up", StringComparison.Ordinal));
    }

    /// <summary>
    /// The point of leaving it alone: fighting a fullscreen window was the whole defect, so it must
    /// not be handed a tile it will only snap back from.
    /// </summary>
    [Fact]
    public void AFullscreenWindow_IsNotRepositioned()
    {
        using var h = new Harness();

        h.GoFullscreen(Monitor);
        var before = h.Chrome.SetPositionCallCount;
        h.Poll(EnoughRounds);

        Assert.Equal(before, h.Chrome.SetPositionCallCount);
    }

    /// <summary>
    /// Measured: it can settle one pixel short of the monitor, so "covers it exactly" is not a
    /// safe test for fullscreen.
    /// </summary>
    [Fact]
    public void AFullscreenWindowOnePixelShortOfTheMonitor_IsStillFullscreen()
    {
        using var h = new Harness();

        h.GoFullscreen(Rectangle.FromSize(Monitor.Left, Monitor.Top, Monitor.Width, Monitor.Height - 1));
        var before = h.Chrome.SetPositionCallCount;
        h.Poll(EnoughRounds);

        Assert.True(h.ChromeInTree);
        Assert.Equal(before, h.Chrome.SetPositionCallCount);
        Assert.DoesNotContain(h.Trace.Lines, line => line.Contains("gave up", StringComparison.Ordinal));
    }

    /// <summary>
    /// The tolerance is a small one, not an open door: a window that stops well short of the monitor
    /// is not fullscreen and is put back on its tile like any other.
    /// </summary>
    [Fact]
    public void AWindowWellShortOfTheMonitor_IsNotFullscreen()
    {
        using var h = new Harness();

        h.Chrome.SimulateFullscreen(Rectangle.FromSize(Monitor.Left, Monitor.Top, Monitor.Width, Monitor.Height - 10));
        var before = h.Chrome.SetPositionCallCount;
        h.Workspace.RaiseWindowBoundsChanged(h.Chrome);

        Assert.True(h.Chrome.SetPositionCallCount > before);
        Assert.Equal(h.Tile, h.Chrome.Bounds);
    }

    /// <summary>
    /// The regression the report was about. Chrome restores its own pre-fullscreen bounds, which are
    /// not the tile, and its style regains the caption; the next bounds change has to find it still
    /// in the tree and put it back.
    /// </summary>
    [Fact]
    public void AWindowThatLeavesFullscreen_LandsBackOnItsTile()
    {
        using var h = new Harness();
        h.GoFullscreen(Monitor);
        h.Poll(EnoughRounds);
        var before = h.Chrome.SetPositionCallCount;

        h.Chrome.SnapsBackTo = null;
        h.Chrome.SimulateLeaveFullscreen(Rectangle.FromSize(200, 100, 800, 600));
        h.Workspace.RaiseWindowBoundsChanged(h.Chrome);

        Assert.True(h.ChromeInTree);
        Assert.True(h.Chrome.SetPositionCallCount > before);
        Assert.Equal(h.Tile, h.Chrome.LastSetPosition);
        Assert.Equal(h.Tile, h.Chrome.Bounds);
    }

    /// <summary>
    /// A maximised window on a monitor with an auto-hide taskbar covers the monitor too, and keeps
    /// its caption and <c>WS_MAXIMIZE</c>. It is an ordinary window with an ordinary verdict.
    /// </summary>
    [Fact]
    public void AMaximizedWindowCoveringTheMonitor_IsNotFullscreen()
    {
        using var h = new Harness();

        h.Chrome.SimulateMaximize(Monitor);
        var before = h.Chrome.SetPositionCallCount;
        h.Workspace.RaiseWindowBoundsChanged(h.Chrome);

        Assert.True(h.Chrome.SetPositionCallCount > before);
        Assert.Equal(h.Tile, h.Chrome.Bounds);
        Assert.DoesNotContain(h.Trace.Lines, line => line.StartsWith("fullscreen", StringComparison.Ordinal));
    }

    /// <summary>
    /// The caption is half of the evidence and covering the monitor is not the other half: a window
    /// that keeps its caption and is merely as big as the monitor is not asking to be left alone.
    /// </summary>
    [Fact]
    public void ACaptionedWindowCoveringTheMonitor_IsNotFullscreen()
    {
        using var h = new Harness();

        h.Chrome.SimulateExternalMove(Monitor);
        var before = h.Chrome.SetPositionCallCount;
        h.Workspace.RaiseWindowBoundsChanged(h.Chrome);

        Assert.True(h.Chrome.SetPositionCallCount > before);
        Assert.Equal(h.Tile, h.Chrome.Bounds);
        Assert.DoesNotContain(h.Trace.Lines, line => line.StartsWith("fullscreen", StringComparison.Ordinal));
    }

    /// <summary>
    /// And the reverse: <c>WS_MAXIMIZE</c> on its own settles it, whatever the caption says.
    /// </summary>
    [Fact]
    public void AMaximizedWindowWithoutACaption_IsNotFullscreen()
    {
        using var h = new Harness();

        h.Chrome.SimulateFullscreen(Monitor);
        h.Chrome.SimulateMaximize(Monitor);
        var before = h.Chrome.SetPositionCallCount;
        h.Workspace.RaiseWindowBoundsChanged(h.Chrome);

        Assert.True(h.Chrome.SetPositionCallCount > before);
        Assert.Equal(h.Tile, h.Chrome.Bounds);
    }

    /// <summary>
    /// The guard is not a blanket amnesty. A window that keeps its caption and never reaches its
    /// tile is exactly what the fighting-window circuit breaker exists for.
    /// </summary>
    [Fact]
    public void ACaptionedWindowThatNeverReachesItsTile_IsStillGivenUpOn()
    {
        using var h = new Harness();

        h.Chrome.SnapsBackTo = Rectangle.FromSize(5000, 5000, 960, 1080);
        h.Poll(EnoughRounds);

        Assert.False(h.ChromeInTree);
        Assert.Contains(h.Trace.Lines, line => line.Contains("gave up", StringComparison.Ordinal));
    }

    /// <summary>
    /// The reconcile poll reports the same fullscreen window every two seconds for as long as it
    /// stays there. One line per entry, not one per poll: the trace stopped being readable the last
    /// time something benign was logged on a tick.
    /// </summary>
    [Fact]
    public void TheFullscreenTraceLine_IsWrittenOnceNotOncePerEvent()
    {
        using var h = new Harness();

        h.GoFullscreen(Monitor);
        h.Poll(EnoughRounds);

        Assert.Equal(1, h.FullscreenLines);
    }

    /// <summary>
    /// Once per ENTRY, so a window that leaves fullscreen and goes back in is reported again.
    /// </summary>
    [Fact]
    public void ReEnteringFullscreen_IsTracedAgain()
    {
        using var h = new Harness();
        h.GoFullscreen(Monitor);
        h.Poll(3);

        h.Chrome.SnapsBackTo = null;
        h.Chrome.SimulateLeaveFullscreen(Rectangle.FromSize(200, 100, 800, 600));
        h.Workspace.RaiseWindowBoundsChanged(h.Chrome);
        h.GoFullscreen(Monitor);
        h.Poll(3);

        Assert.Equal(2, h.FullscreenLines);
    }

    // -----------------------------------------------------------------------------------------
    // U1: admitted already fullscreen. OnWindowAdded never asked IsFullscreen, so a game that
    // launches straight into fullscreen was shrunk to its tile by the admission arrange.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// A window whose FIRST appearance is already fullscreen (no caption, not maximised, covering
    /// the monitor) must not be shrunk to a tile by the admission arrange.
    /// </summary>
    [Fact]
    public void AWindowAdmittedAlreadyFullscreen_IsNotRepositioned()
    {
        using var h = new Harness();

        var game = new RecordingWindow(new IntPtr(30), Monitor, className: "Game", processName: "game.exe");
        h.Workspace.RaiseWindowAdded(game);

        Assert.Equal(0, game.SetPositionCallCount);
        Assert.Equal(Monitor, game.Bounds);
    }

    /// <summary>
    /// Recorded exactly like any other fullscreen entry -- the same trace line, once -- not
    /// silently skipped because it arrived on the admission path instead of a poll.
    /// </summary>
    [Fact]
    public void AWindowAdmittedAlreadyFullscreen_IsTracedAsFullscreenOnce()
    {
        using var h = new Harness();

        var game = new RecordingWindow(new IntPtr(30), Monitor, className: "Game", processName: "game.exe");
        h.Workspace.RaiseWindowAdded(game);

        Assert.Equal(1, h.Trace.Lines.Count(
            line => line.StartsWith("fullscreen hwnd=0x1E ", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Every other admission writes an `added` line once it finishes; this path used to return
    /// before ever reaching it. Same shape as the ordinary line, but says the window was left
    /// alone because it is fullscreen, since nothing of its own was actually moved.
    /// </summary>
    [Fact]
    public void AWindowAdmittedAlreadyFullscreen_IsTracedAsAdded()
    {
        using var h = new Harness();

        var game = new RecordingWindow(new IntPtr(30), Monitor, className: "Game", processName: "game.exe");
        h.Workspace.RaiseWindowAdded(game);

        Assert.Contains(h.Trace.Lines, line => line ==
            "added hwnd=0x1E class=Game proc=game.exe [L=0 T=0 W=1920 H=1080] -> left alone (fullscreen)");
    }

    /// <summary>
    /// It still gets a leaf -- InsertWindow runs before the fullscreen check turns the arrange's
    /// SetPosition off -- so the tree already has a tile on record for it, and leaving fullscreen
    /// lands it there like any other entry, not through a second admission.
    /// </summary>
    [Fact]
    public void AWindowAdmittedAlreadyFullscreen_StillGetsALeafAndLandsOnItOnceItLeaves()
    {
        using var h = new Harness();

        var game = new RecordingWindow(new IntPtr(30), Monitor, className: "Game", processName: "game.exe");
        h.Workspace.RaiseWindowAdded(game);

        // Admitted, but not yet moved: the leaf exists and already has a tile on record, while the
        // window itself is still untouched (proven again here, not just in the sibling fact, so a
        // mutation that skipped the leaf/tile half of this while still leaving the window alone
        // cannot pass by accident).
        Assert.True(h.Registry.TryGetLeaf(game.Handle, out var leaf) && leaf is not null);
        Assert.Equal(0, game.SetPositionCallCount);
        var tile = TreeArranger.TileOf(leaf!);
        var expectedTile = Rectangle.FromSize(tile.X, tile.Y, tile.Width, tile.Height);

        game.SimulateLeaveFullscreen(Rectangle.FromSize(50, 50, 400, 300));
        h.Workspace.RaiseWindowBoundsChanged(game);

        Assert.True(game.SetPositionCallCount > 0);
        Assert.Equal(expectedTile, game.Bounds);
    }

    // -----------------------------------------------------------------------------------------
    // U2: neighbour reflow. Every arrange repositioned every leaf, the fullscreen one included,
    // once per neighbour opening or closing.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// A neighbour OPENING while Chrome is fullscreen must not touch Chrome -- only reflow the
    /// ordinary siblings around it.
    /// </summary>
    [Fact]
    public void ANeighbourOpening_DoesNotRepositionAFullscreenWindow()
    {
        using var h = new Harness();
        h.GoFullscreen(Monitor);
        h.Poll(1);
        var before = h.Chrome.SetPositionCallCount;

        var third = new RecordingWindow(new IntPtr(40), Rectangle.FromSize(0, 0, 400, 300), style: CaptionedStyle);
        h.Workspace.RaiseWindowAdded(third);

        Assert.Equal(before, h.Chrome.SetPositionCallCount);
        Assert.True(third.SetPositionCallCount > 0);
    }

    /// <summary>
    /// A neighbour CLOSING while Chrome is fullscreen must not touch Chrome either, while the
    /// surviving neighbour still reflows into the space it freed.
    /// </summary>
    [Fact]
    public void ANeighbourClosing_DoesNotRepositionAFullscreenWindow()
    {
        using var h = new Harness();
        var third = new RecordingWindow(new IntPtr(40), Rectangle.FromSize(0, 0, 400, 300), style: CaptionedStyle);
        h.Workspace.RaiseWindowAdded(third);

        h.GoFullscreen(Monitor);
        h.Poll(1);

        var chromeBefore = h.Chrome.SetPositionCallCount;
        var thirdBefore = third.SetPositionCallCount;

        h.Workspace.RaiseWindowRemoved(h.Neighbour);

        Assert.Equal(chromeBefore, h.Chrome.SetPositionCallCount);
        Assert.True(third.SetPositionCallCount > thirdBefore);
    }

    // -----------------------------------------------------------------------------------------
    // U3: non-owner display. IsFullscreen compared only against _owners[handle]'s display, so a
    // window fullscreen on a monitor other than the one its tree is filed under was never
    // recognized.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// A window owned by display A (its tree is filed there) that goes fullscreen covering display
    /// B is still recognized: not repositioned, not given up on however long it stays there, and
    /// traced once -- exactly as if it had gone fullscreen on its own display.
    /// </summary>
    [Fact]
    public void AWindowFullscreenOnANonOwnerDisplay_IsRecognized()
    {
        var left = new FakeDisplay(new IntPtr(1), Monitor, WorkArea, 1.0, true);
        var rightBounds = Rectangle.FromSize(1920, 0, 2560, 1440);
        var right = new FakeDisplay(new IntPtr(2), rightBounds, rightBounds, 1.0, false);

        var registry = new WindowRegistry();
        var trees = new TreeManager([left, right], left, registry);
        var workspace = new FakeWorkspace();
        var trace = new RecordingTrace();
        using var adapter = new MultiMonitorWorkspaceAdapter(
            workspace, trees, registry, () => ExceptionList.Empty, () => false, () => null)
        {
            Trace = trace,
        };

        var neighbour = new RecordingWindow(new IntPtr(10), Rectangle.FromSize(0, 0, 400, 300), style: CaptionedStyle);
        var chrome = new RecordingWindow(
            new IntPtr(20), Rectangle.FromSize(0, 0, 400, 300),
            className: "Chrome_WidgetWin_1", processName: "chrome.exe", style: CaptionedStyle);
        workspace.RaiseWindowAdded(neighbour);
        workspace.RaiseWindowAdded(chrome);

        // Chrome's tree is filed under LEFT (its admission bounds resolved there), but it goes
        // fullscreen on RIGHT -- dragged there, or launched onto it directly.
        chrome.SimulateFullscreen(rightBounds);
        chrome.SnapsBackTo = rightBounds;

        var before = chrome.SetPositionCallCount;
        for (var round = 0; round < EnoughRounds; round++)
        {
            workspace.RaiseWindowBoundsChanged(chrome);
        }

        Assert.Equal(before, chrome.SetPositionCallCount);
        Assert.True(registry.TryGetLeaf(chrome.Handle, out _));
        Assert.DoesNotContain(trace.Lines, line => line.Contains("gave up", StringComparison.Ordinal));
        Assert.Equal(1, trace.Lines.Count(
            line => line.StartsWith("fullscreen hwnd=0x14 ", StringComparison.Ordinal)));
    }

    // -----------------------------------------------------------------------------------------
    // R3-001, the unproved path: a floor already on record. AddWindow's fullscreen early return
    // sat BEFORE DoesNotFitItsFloor, so a window re-admitted while fullscreen skipped that check
    // entirely -- unlike the ordinary (non-fullscreen) re-admission of the very same window.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Baseline for the comparison below: a NON-fullscreen re-admission of a window whose floor
    /// is already on record, and still does not fit any tile with its neighbour present, is
    /// turned away before anything of its own is moved -- no extra <c>SetPosition</c> call, and
    /// no leaf.
    /// </summary>
    [Fact]
    public void AWindowWithARecordedFloor_ReAdmittedNonFullscreen_IsTurnedAwayBeforeAnythingMoves()
    {
        var primary = new FakeDisplay(new IntPtr(1), Monitor, WorkArea, 1.0, true);
        var registry = new WindowRegistry();
        var trees = new TreeManager([primary], primary, registry);
        var workspace = new FakeWorkspace();
        using var adapter = new MultiMonitorWorkspaceAdapter(
            workspace, trees, registry, () => ExceptionList.Empty, () => false, () => null);

        var neighbour = new RecordingWindow(new IntPtr(10), Rectangle.FromSize(0, 0, 400, 300), style: CaptionedStyle);
        var constrained = new RecordingWindow(new IntPtr(20), Rectangle.FromSize(0, 0, 400, 300), style: CaptionedStyle);
        // Wider than either half-tile beside a neighbour, narrower than the whole work area -- the
        // same shape MinimumSizeWindowTests.Floor uses, so no regroup or share can ever reach it.
        constrained.MinimumSize = (1800, 100);

        workspace.RaiseWindowAdded(neighbour);
        workspace.RaiseWindowAdded(constrained);

        // Two misses records the floor and parks it (MinimumSizeWindowTests' own shape).
        workspace.RaiseWindowBoundsChanged(constrained);
        workspace.RaiseWindowBoundsChanged(constrained);
        Assert.False(registry.TryGetLeaf(constrained.Handle, out _));

        var before = constrained.SetPositionCallCount;
        workspace.RaiseWindowAdded(constrained);

        Assert.False(registry.TryGetLeaf(constrained.Handle, out _));
        Assert.Equal(before, constrained.SetPositionCallCount);
    }

    /// <summary>
    /// The unproved path itself. Re-admitted while ALSO fullscreen, the same window with the same
    /// unfit floor must end up EQUIVALENT to the baseline above -- turned away, no leaf, no extra
    /// <c>SetPosition</c> call -- not squatting on a leaf whose tile it can never fit, which is
    /// what the earlier ordering (fullscreen checked before the floor) produced: two rounds
    /// positioning it into an over-floor rectangle before a third round finally parked it.
    /// </summary>
    [Fact]
    public void AWindowWithARecordedFloor_ReAdmittedFullscreen_IsAlsoTurnedAwayBeforeAnythingMoves()
    {
        var primary = new FakeDisplay(new IntPtr(1), Monitor, WorkArea, 1.0, true);
        var registry = new WindowRegistry();
        var trees = new TreeManager([primary], primary, registry);
        var workspace = new FakeWorkspace();
        using var adapter = new MultiMonitorWorkspaceAdapter(
            workspace, trees, registry, () => ExceptionList.Empty, () => false, () => null);

        var neighbour = new RecordingWindow(new IntPtr(10), Rectangle.FromSize(0, 0, 400, 300), style: CaptionedStyle);
        var constrained = new RecordingWindow(new IntPtr(20), Rectangle.FromSize(0, 0, 400, 300), style: CaptionedStyle);
        constrained.MinimumSize = (1800, 100);

        workspace.RaiseWindowAdded(neighbour);
        workspace.RaiseWindowAdded(constrained);

        workspace.RaiseWindowBoundsChanged(constrained);
        workspace.RaiseWindowBoundsChanged(constrained);
        Assert.False(registry.TryGetLeaf(constrained.Handle, out _));

        var before = constrained.SetPositionCallCount;
        constrained.SnapsBackTo = Monitor;
        constrained.SimulateFullscreen(Monitor);
        workspace.RaiseWindowAdded(constrained);

        Assert.False(registry.TryGetLeaf(constrained.Handle, out _));
        Assert.Equal(before, constrained.SetPositionCallCount);
    }

    // -----------------------------------------------------------------------------------------
    // R3-003: the turned-away outcome above is not the only one a recorded floor can produce on
    // re-admission. It can also FIT once the tree is reshaped -- given a branch of its own by
    // TryRegroupToFit -- while the window is fullscreen. That must cost it exactly as little as
    // being turned away: TryRegroupToFit only reshapes the TREE through the pure
    // TreeArranger.Arrange, never SetPosition, so a fullscreen re-admission that ends up fitting
    // is exactly as untouched as one that does not.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// The unproved success path. The floor recorded above (1800 wide) does not fit beside a
    /// single neighbour -- too wide for any share a lone donor can spare -- so the window is
    /// turned away and parked, exactly like the baseline. A second neighbour then arrives, and
    /// re-admitting the parked window WHILE FULLSCREEN finds three children where the recorded
    /// floor was measured against only two: TryRegroupToFit gives it a branch of its own, full
    /// width, and the same 1800 now fits. It must still keep its leaf, receive no
    /// <c>SetPosition</c> of its own, and be recorded as fullscreen exactly like an ordinary
    /// fullscreen admission -- proving the "floor helpers never position" property the fix
    /// relies on even on the outcome where a floor helper actually answers yes.
    /// </summary>
    [Fact]
    public void AWindowWithARecordedFloor_ReAdmittedFullscreen_FitsAfterRegrouping_AndIsRecordedAsFullscreen()
    {
        var primary = new FakeDisplay(new IntPtr(1), Monitor, WorkArea, 1.0, true);
        var registry = new WindowRegistry();
        var trees = new TreeManager([primary], primary, registry);
        var workspace = new FakeWorkspace();
        var trace = new RecordingTrace();
        using var adapter = new MultiMonitorWorkspaceAdapter(
            workspace, trees, registry, () => ExceptionList.Empty, () => false, () => null)
        {
            Trace = trace,
        };

        var neighbour = new RecordingWindow(new IntPtr(10), Rectangle.FromSize(0, 0, 400, 300), style: CaptionedStyle);
        var constrained = new RecordingWindow(new IntPtr(20), Rectangle.FromSize(0, 0, 400, 300), style: CaptionedStyle);
        // Same shape as the turned-away fact above: wide enough that no share of a TWO-window
        // group can ever reach it, narrower than the whole work area -- so it does not fit beside
        // one lone neighbour, but does fit once given the full width of a branch of its own.
        constrained.MinimumSize = (1800, 100);

        workspace.RaiseWindowAdded(neighbour);
        workspace.RaiseWindowAdded(constrained);

        // Two misses records the floor and parks it -- with only one sibling, fewer than three
        // children means no branch is possible yet, and no two-window share reaches 1800.
        workspace.RaiseWindowBoundsChanged(constrained);
        workspace.RaiseWindowBoundsChanged(constrained);
        Assert.False(registry.TryGetLeaf(constrained.Handle, out _));

        // A second neighbour arrives while constrained is still parked, so re-admission finds
        // THREE children in the flat group -- the one thing the recorded floor was missing.
        var second = new RecordingWindow(new IntPtr(40), Rectangle.FromSize(0, 0, 400, 300), style: CaptionedStyle);
        workspace.RaiseWindowAdded(second);

        var before = constrained.SetPositionCallCount;
        constrained.SnapsBackTo = Monitor;
        constrained.SimulateFullscreen(Monitor);
        workspace.RaiseWindowAdded(constrained);

        Assert.True(registry.TryGetLeaf(constrained.Handle, out var leaf) && leaf is not null);
        Assert.Equal(before, constrained.SetPositionCallCount);
        Assert.Contains(trace.Lines, line => line.StartsWith("regrouped hwnd=0x14 ", StringComparison.Ordinal));
        Assert.Equal(1, trace.Lines.Count(
            line => line.StartsWith("fullscreen hwnd=0x14 ", StringComparison.Ordinal)));
        Assert.Contains(trace.Lines, line =>
            line.StartsWith("added hwnd=0x14 ", StringComparison.Ordinal) &&
            line.EndsWith("-> left alone (fullscreen)", StringComparison.Ordinal));
    }

    // -----------------------------------------------------------------------------------------
    // TryGrowToFit's own success is not covered here as a second fact beside the one above. With
    // fewer than three children TryRegroupToFit is skipped by construction (Children.Count < 3),
    // and TryGrowToFit's reach with a single fixed donor and an unchanged work area is the same
    // number whichever admission asks for it -- there is no fixture change that flips it from a
    // miss to a fit without either changing the display (a different feature) or adding a third
    // child, which hands the attempt to TryRegroupToFit first (it runs before TryGrowToFit, and
    // per its own code mutates the tree via ExtractToOppositeAxis even on the branch that goes on
    // to fail its own fit-check, so a third child no longer measures TryGrowToFit in isolation
    // either). The regroup fact above already proves the shared property both helpers rely on --
    // that DoesNotFitItsFloor's own fit-check, which both call, never positions a window.
    // -----------------------------------------------------------------------------------------
}
