using CosmicWin.App.Diagnostics;
using CosmicWin.App.Input;
using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.App.Tray;
using CosmicWin.Interop;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App.Tests;

/// <summary>
/// The taskbar hiding, moving to another edge or being resized, driven through the composition the
/// app really runs: the tick asks the displays what they look like now, and the layout follows.
/// </summary>
/// <remarks>
/// Written at the composition level because the fix is a WIRING one. <see cref="TreeManager"/> could
/// already reflow a display when told its work area changed; nothing ever told it, and the displays
/// themselves were read once at startup. A unit test of either half passes on a composition that
/// forgot to connect them.
/// </remarks>
public sealed class WorkAreaTrackingTests
{
    private static readonly Rectangle TaskbarAtTheBottom = Rectangle.FromSize(0, 0, 1920, 1040);

    private sealed class Scheduler
    {
        private Action? _callback;

        public IDisposable Schedule(TimeSpan interval, Action callback)
        {
            _callback = callback;
            return new NullDisposable();
        }

        public void Fire() => _callback!();
    }

    private sealed class NullDisposable : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class RecordingTrace : IDesktopTrace
    {
        public List<string> Lines { get; } = [];

        public void Record(string line) => Lines.Add(line);
    }

    private sealed class StubForeground : IForegroundWindowSource
    {
        public nint GetForegroundHandle() => 0;
    }

    private sealed record Harness(
        AppComposition Composition, TrayMenuController Tray, FakeWorkspace Workspace, Scheduler Scheduler,
        FakeDisplay Primary, FakeDisplay Secondary, RecordingTrace Trace, List<IDisplay> ChangedNextTick)
    {
        /// <summary>Moves the taskbar: rewrites the display, reports it as changed, and lets one tick pass.</summary>
        public void MoveTaskbar(FakeDisplay display, Rectangle workArea)
        {
            display.WorkArea = workArea;
            ChangedNextTick.Add(display);
            Scheduler.Fire();
        }
    }

    private static Harness Wire()
    {
        var primary = new FakeDisplay(
            new IntPtr(1), Rectangle.FromSize(0, 0, 1920, 1080), TaskbarAtTheBottom, 1.0, true);
        var secondary = new FakeDisplay(
            new IntPtr(2), Rectangle.FromSize(1920, 0, 1280, 720), Rectangle.FromSize(1920, 0, 1280, 720), 1.0, false);
        var workspace = new FakeWorkspace();
        var registry = new WindowRegistry();
        var treeManager = new TreeManager([primary, secondary], primary, registry);
        var scheduler = new Scheduler();
        var trace = new RecordingTrace();
        var platform = new FakeKeyboardHookPlatform();
        var changedNextTick = new List<IDisplay>();
        TrayMenuController? tray = null;

        var composition = AppComposition.Wire(
            workspace, treeManager, registry, new StubForeground(), new ExceptionListStore(ExceptionList.Empty),
            focusTrace: new RecordingFocusTrace(),
            disableTaskTrigger: () => { },
            scheduleReconcile: scheduler.Schedule,
            hookFactory: writer => new LowLevelKeyboardHook(writer, platform, TimeSpan.FromSeconds(5), () => 0),
            loadExceptions: () => ExceptionList.Empty,
            shutdown: () => { },
            buildTray: controller =>
            {
                tray = controller;
                return new NullDisposable();
            },
            desktopTrace: trace,
            // Drains what the test queued, the way the real refresh reports each change exactly once.
            refreshDisplays: () =>
            {
                var changed = changedNextTick.ToList();
                changedNextTick.Clear();
                return changed;
            });

        return new Harness(composition, tray!, workspace, scheduler, primary, secondary, trace, changedNextTick);
    }

    private static RecordingWindow TileOnPrimary(Harness harness, int handle = 1001)
    {
        var window = new RecordingWindow(new IntPtr(handle), Rectangle.FromSize(100, 100, 400, 300));
        harness.Workspace.RaiseWindowAdded(window);
        return window;
    }

    /// <summary>A lone window fills its display's work area, inset by the gap at every edge.</summary>
    private static Rectangle Filling(Rectangle workArea) =>
        new(
            workArea.Left + TreeArranger.Gap, workArea.Top + TreeArranger.Gap,
            workArea.Right - TreeArranger.Gap, workArea.Bottom - TreeArranger.Gap);

    /// <summary>
    /// Every shape the user can give the taskbar, from the bottom strip the display started with:
    /// hidden, on the top, on the left, on the right, and the bottom strip made taller.
    /// </summary>
    public static TheoryData<int, int, int, int> TaskbarShapes => new()
    {
        { 0, 0, 1920, 1080 },
        { 0, 40, 1920, 1080 },
        { 48, 0, 1920, 1080 },
        { 0, 0, 1872, 1080 },
        { 0, 0, 1920, 960 },
    };

    [Theory]
    [MemberData(nameof(TaskbarShapes))]
    public void TheTiledWindowIsRefitToWhatTheTaskbarLeaves(int left, int top, int right, int bottom)
    {
        var harness = Wire();
        using (harness.Composition)
        {
            var window = TileOnPrimary(harness);
            Assert.Equal(Filling(TaskbarAtTheBottom), window.LastSetPosition);
            var newArea = new Rectangle(left, top, right, bottom);

            harness.MoveTaskbar(harness.Primary, newArea);

            Assert.Equal(Filling(newArea), window.LastSetPosition);
        }
    }

    /// <summary>
    /// The negative that keeps the fact above honest: a tick that reports nothing must not touch a
    /// window. Without it "reflows on every tick" would satisfy the fact above just as well.
    /// </summary>
    [Fact]
    public void ATickThatReportsNoChange_MovesNothing()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            var window = TileOnPrimary(harness);
            var calls = window.SetPositionCallCount;

            harness.Scheduler.Fire();
            harness.Scheduler.Fire();

            Assert.Equal(calls, window.SetPositionCallCount);
        }
    }

    /// <summary>Monitors are independent: one taskbar moving is no reason to move another display's windows.</summary>
    [Fact]
    public void OnlyTheChangedMonitorIsReflowed()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            var onPrimary = TileOnPrimary(harness, 1001);
            var onSecondary = new RecordingWindow(new IntPtr(1002), Rectangle.FromSize(2000, 100, 400, 300));
            harness.Workspace.RaiseWindowAdded(onSecondary);
            var primaryCalls = onPrimary.SetPositionCallCount;

            var shrunk = Rectangle.FromSize(1920, 0, 1280, 680);
            harness.MoveTaskbar(harness.Secondary, shrunk);

            Assert.Equal(Filling(shrunk), onSecondary.LastSetPosition);
            Assert.Equal(primaryCalls, onPrimary.SetPositionCallCount);
        }
    }

    /// <summary>
    /// Tiling off means the user is free to arrange windows by hand, and a taskbar that moves is no
    /// licence to shove them around. Switching it back on puts the layout back, and that must use
    /// the taskbar as it is NOW, not as it was when tiling went off.
    /// </summary>
    [Fact]
    public void WithTilingOff_TheWindowIsLeftAlone_AndFitsTheNewTaskbarWhenTilingComesBack()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            var window = TileOnPrimary(harness);
            harness.Tray.ToggleTiling();
            var calls = window.SetPositionCallCount;
            var hidden = Rectangle.FromSize(0, 0, 1920, 1080);

            harness.MoveTaskbar(harness.Primary, hidden);
            Assert.Equal(calls, window.SetPositionCallCount);

            harness.Tray.ToggleTiling();

            Assert.Equal(Filling(hidden), window.LastSetPosition);

            // Resuming already laid the display out on the new area; the change must not be
            // applied a second time by the tick that follows.
            var afterResume = window.SetPositionCallCount;
            harness.Scheduler.Fire();
            Assert.Equal(afterResume, window.SetPositionCallCount);
        }
    }

    /// <summary>
    /// A paused CosmicWin touches nothing, but the change must not be lost with it: the display has
    /// already reported it once and will not again, so dropping it on the floor would leave the
    /// layout wrong until something unrelated happened to reflow it.
    /// </summary>
    [Fact]
    public void WhilePaused_TheWindowIsLeftAlone_AndCatchesUpWhenTheNextTickAfterResuming()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            var window = TileOnPrimary(harness);
            harness.Tray.TogglePause();
            var calls = window.SetPositionCallCount;
            var hidden = Rectangle.FromSize(0, 0, 1920, 1080);

            harness.MoveTaskbar(harness.Primary, hidden);
            Assert.Equal(calls, window.SetPositionCallCount);

            harness.Tray.TogglePause();
            harness.Scheduler.Fire();

            Assert.Equal(Filling(hidden), window.LastSetPosition);
        }
    }

    /// <summary>The instrument: a reflow the user did not ask for has to be explainable from the trace afterwards.</summary>
    [Fact]
    public void TheChangeIsRecordedInTheTrace()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            harness.MoveTaskbar(harness.Primary, Rectangle.FromSize(0, 0, 1920, 1080));

            var line = Assert.Single(harness.Trace.Lines, l => l.Contains("work area", StringComparison.Ordinal));
            Assert.Contains("0x1", line, StringComparison.Ordinal);
        }
    }
}
