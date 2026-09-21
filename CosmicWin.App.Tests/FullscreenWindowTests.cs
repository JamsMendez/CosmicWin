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
}
