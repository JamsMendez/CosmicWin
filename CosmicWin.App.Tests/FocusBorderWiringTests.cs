using System.Threading.Channels;
using CosmicWin.App.Input;
using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.App.Tray;
using CosmicWin.Interop;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App.Tests;

/// <summary>
/// The focus border follows focus, and gets out of the way when there is nothing to frame.
/// </summary>
/// <remarks>
/// The drawing itself needs a live WPF window, a real HWND and a display, and is verified by hand
/// like the rest of the tray. What can be wrong HERE is everything around it: which window it
/// frames, whether it lets go, and whether it respects the pause the whole app respects.
/// </remarks>
public sealed class FocusBorderWiringTests
{
    private sealed record Call(Rectangle Window, double Scaling, int Thickness);

    private sealed class RecordingFocusBorder : IFocusBorder
    {
        public List<Call> Shown { get; } = [];

        public int HideCallCount { get; private set; }

        public void ShowAround(Rectangle window, double scaling, int thickness) =>
            Shown.Add(new Call(window, scaling, thickness));

        public void Hide() => HideCallCount++;

        public void Dispose()
        {
        }
    }

    private sealed class NoForeground : IForegroundWindowSource
    {
        public nint Handle { get; set; }

        public nint GetForegroundHandle() => Handle;
    }

    private sealed class NullDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class ImmediateScheduler
    {
        private Action? _callback;

        public IDisposable Schedule(TimeSpan interval, Action callback)
        {
            _callback = callback;
            return new NullDisposable();
        }

        public void Fire() => _callback!();
    }

    private sealed record Harness(
        AppComposition Composition, FakeWorkspace Workspace, NoForeground Foreground,
        RecordingFocusBorder Border, ImmediateScheduler Scheduler, LowLevelKeyboardHook Hook,
        FakeKeyboardHookPlatform Platform);

    private static Harness Wire()
    {
        var workspace = new FakeWorkspace();
        var primary = new FakeDisplay(
            new IntPtr(1), Rectangle.FromSize(0, 0, 1920, 1080), Rectangle.FromSize(0, 0, 1920, 1080), 1.5, true);
        var registry = new WindowRegistry();
        var treeManager = new TreeManager([primary], primary, registry);
        var foreground = new NoForeground();
        var border = new RecordingFocusBorder();
        var scheduler = new ImmediateScheduler();
        LowLevelKeyboardHook? hook = null;
        var platform = new FakeKeyboardHookPlatform();

        var composition = AppComposition.Wire(
            workspace, treeManager, registry, foreground, new ExceptionListStore(ExceptionList.Empty),
            focusTrace: new RecordingFocusTrace(),
            disableTaskTrigger: () => { },
            scheduleReconcile: scheduler.Schedule,
            hookFactory: writer =>
            {
                hook = new LowLevelKeyboardHook(writer, platform, TimeSpan.FromSeconds(5), () => 0);
                return hook;
            },
            loadExceptions: () => ExceptionList.Empty,
            shutdown: () => { },
            buildTray: _ => new NullDisposable(),
            focusBorder: border);

        return new Harness(composition, workspace, foreground, border, scheduler, hook!, platform);
    }

    [Fact]
    public void TheBorderFramesTheFocusedWindow_AtItsDisplaysScaling()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            var window = new RecordingWindow(new IntPtr(0xB01), Rectangle.FromSize(0, 0, 800, 600));
            harness.Workspace.RaiseWindowAdded(window);
            harness.Foreground.Handle = window.Handle;

            harness.Scheduler.Fire();

            var call = harness.Border.Shown[^1];
            Assert.Equal(window.Bounds, call.Window);
            Assert.Equal(1.5, call.Scaling);
            Assert.Equal(BorderGeometry.DefaultThickness, call.Thickness);
        }
    }

    /// <summary>Nothing focused is not nothing to do: the border has to let go, or it hangs over a window that is gone.</summary>
    [Fact]
    public void WithNothingFocused_TheBorderIsHidden()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            harness.Scheduler.Fire();

            Assert.Empty(harness.Border.Shown);
            Assert.True(harness.Border.HideCallCount > 0);
        }
    }

    /// <summary>Pause means hands off, and a border left drawn would say the opposite on screen.</summary>
    [Fact]
    public void WhilePaused_TheBorderIsHidden()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            var window = new RecordingWindow(new IntPtr(0xB02), Rectangle.FromSize(0, 0, 800, 600));
            harness.Workspace.RaiseWindowAdded(window);
            harness.Foreground.Handle = window.Handle;
            harness.Scheduler.Fire();
            var framed = harness.Border.Shown.Count;

            harness.Hook.IsPaused = true;
            harness.Scheduler.Fire();

            Assert.Equal(framed, harness.Border.Shown.Count);
            Assert.True(harness.Border.HideCallCount > 0);
        }
    }

    /// <summary>
    /// The fix for the reported lag. A chord that re-lays the tree must move the border with it,
    /// not leave it on the old rectangle until the next reconciliation tick.
    /// </summary>
    /// <remarks>
    /// Reported from real use, and worst on <c>Alt+O</c>, where toggling the split axis moves every
    /// window at once and the border sat behind all of them for up to a full interval. The
    /// scheduler is deliberately NEVER fired here: if the border only moved on the tick, this fact
    /// would time out.
    /// </remarks>
    [Fact]
    public async Task AChordMovesTheBorderWithoutWaitingForTheTick()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            var left = new RecordingWindow(new IntPtr(0xB03), Rectangle.FromSize(0, 0, 960, 1080));
            var right = new RecordingWindow(new IntPtr(0xB04), Rectangle.FromSize(960, 0, 960, 1080));
            harness.Workspace.RaiseWindowAdded(left);
            harness.Workspace.RaiseWindowAdded(right);
            harness.Foreground.Handle = left.Handle;
            harness.Border.Shown.Clear();

            Assert.True(harness.Platform.Raise(KeyboardKey.O, isKeyDown: true, ModifierKeys.Alt));

            var moved = await WaitUntil(() => harness.Border.Shown.Count > 0, TimeSpan.FromSeconds(2));
            Assert.True(moved, "The border should follow the chord, not the reconciliation tick.");
        }
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        return condition();
    }

    /// <summary>
    /// The border follows the WINDOW, not only the chord that moved it.
    /// </summary>
    /// <remarks>
    /// Reported from real use and worst on Windows Terminal, which ANIMATES its resize: CosmicWin
    /// applies the geometry, the window keeps arriving for several frames, and a border refreshed
    /// once after the chord trails it the whole way. Every frame of that movement raises a bounds
    /// change, so following it is both exact and free of any interval to tune.
    /// </remarks>
    [Fact]
    public void WhenTheFocusedWindowKeepsMoving_TheBorderFollowsEveryFrame()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            var window = new RecordingWindow(new IntPtr(0xB05), Rectangle.FromSize(0, 0, 800, 600));
            harness.Workspace.RaiseWindowAdded(window);
            harness.Foreground.Handle = window.Handle;
            harness.Scheduler.Fire();
            harness.Border.Shown.Clear();

            window.SimulateExternalMove(Rectangle.FromSize(0, 0, 900, 600));
            harness.Workspace.RaiseWindowBoundsChanged(window);

            // Where the window ENDS UP, not where it was passing through. The tiling adapter listens
            // to the same event and was subscribed first, so it snaps the window back into its tile
            // before this handler reads the rectangle -- and a border that followed the transient
            // position would land somewhere the window never stays.
            Assert.NotEmpty(harness.Border.Shown);
            Assert.Equal(window.Bounds, harness.Border.Shown[^1].Window);
        }
    }

    /// <summary>
    /// A reflow nobody asked for still has to move the border: closing a neighbour stretches the
    /// focused window, and the border has to arrive with it.
    /// </summary>
    /// <remarks>
    /// Reported from real use. The two paths that existed both miss this. There is no chord, so
    /// <c>AfterAction</c> never runs; and the bounds event cannot save it either, because
    /// <c>TreeArranger</c> reaches the window through the SAME <c>IWindow</c> instance the workspace
    /// caches, so <c>Win32Window.SetPosition</c> updates <c>Bounds</c> before the WinEvent arrives
    /// and <c>Win32Workspace.UpdateBounds</c> then sees no change to report. That left the
    /// reconciliation tick as the only surviving path, and the border a full interval behind. The
    /// scheduler is deliberately NEVER fired here: if the border only moved on the tick, this fact
    /// would fail.
    /// </remarks>
    [Fact]
    public void WhenClosingANeighbourStretchesTheFocusedWindow_TheBorderFollowsWithoutTheTick()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            var staying = new RecordingWindow(new IntPtr(0xB08), Rectangle.FromSize(0, 0, 1920, 540));
            var closing = new RecordingWindow(new IntPtr(0xB09), Rectangle.FromSize(0, 540, 1920, 540));
            harness.Workspace.RaiseWindowAdded(staying);
            harness.Workspace.RaiseWindowAdded(closing);
            harness.Foreground.Handle = staying.Handle;
            harness.Scheduler.Fire();
            harness.Border.Shown.Clear();

            harness.Workspace.RaiseWindowRemoved(closing);

            Assert.NotEmpty(harness.Border.Shown);
            Assert.Equal(staying.Bounds, harness.Border.Shown[^1].Window);
        }
    }

    /// <summary>
    /// A window the border is NOT on moving is nothing to redraw for. During a reflow every tile
    /// reports a move, and answering all of them is work with nothing to show.
    /// </summary>
    [Fact]
    public void WhenAnUnfocusedWindowMoves_TheBorderIsLeftAlone()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            var focused = new RecordingWindow(new IntPtr(0xB06), Rectangle.FromSize(0, 0, 960, 1080));
            var other = new RecordingWindow(new IntPtr(0xB07), Rectangle.FromSize(960, 0, 960, 1080));
            harness.Workspace.RaiseWindowAdded(focused);
            harness.Workspace.RaiseWindowAdded(other);
            harness.Foreground.Handle = focused.Handle;
            harness.Scheduler.Fire();
            harness.Border.Shown.Clear();

            other.SimulateExternalMove(Rectangle.FromSize(900, 0, 1020, 1080));
            harness.Workspace.RaiseWindowBoundsChanged(other);

            Assert.Empty(harness.Border.Shown);
        }
    }
}
