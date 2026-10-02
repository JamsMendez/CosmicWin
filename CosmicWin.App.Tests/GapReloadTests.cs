using CosmicWin.App.Input;
using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.App.Tray;
using CosmicWin.Interop;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App.Tests;

/// <summary>
/// T5 (maintainer decision 2026-09-26): the new <c>gap</c> settings key, driven
/// through <see cref="AppComposition.Wire"/>'s <c>loadGap</c> parameter and WE-3's existing "Reload"
/// tray trigger -- see <see cref="CompositionRoot.BuildTrayMenuController"/>'s <c>reloadGap</c>
/// parameter, which this file's <see cref="TrayMenuController.Reload"/> calls exercise end to end.
/// </summary>
/// <remarks>
/// Written at this level, mirroring <see cref="TilingModeTests"/>, for the same reason that file is:
/// applying a live gap change is a WIRING decision spanning <c>TreeArranger.Gap</c>, the tray's
/// Reload trigger and the tiling switch's own on/off invariant, and a unit test of any one piece
/// would pass on a composition that forgot how the others fit together.
/// </remarks>
public sealed class GapReloadTests
{
    /// <summary>
    /// T11 (review R3-immediate-scheduler-never-fires): renamed from
    /// "ImmediateScheduler" -- that name promised the reconcile callback fired right away, but it was
    /// only ever stored and discarded, never invoked. Every fact in this file drives everything
    /// through explicit calls (<see cref="Tray"/>'s own methods, <c>Workspace.RaiseWindowAdded</c>),
    /// never through the periodic reconcile tick, so this exists only to satisfy
    /// <c>AppComposition.Wire</c>'s <c>scheduleReconcile</c> parameter with a disposable that does
    /// nothing.
    /// </summary>
    private sealed class NeverFiringReconcileScheduler
    {
        public IDisposable Schedule(TimeSpan interval, Action callback) => new NullDisposable();
    }

    private sealed class NullDisposable : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class Foreground : IForegroundWindowSource
    {
        public nint GetForegroundHandle() => 0;
    }

    /// <summary>Mirrors every other private recording trace fake in this test project (e.g. <c>CompositionRootTests.RecordingDesktopTrace</c>).</summary>
    private sealed class RecordingDesktopTrace : CosmicWin.App.Diagnostics.IDesktopTrace
    {
        public List<string> Lines { get; } = [];

        public void Record(string line) => Lines.Add(line);
    }

    private sealed record Harness(
        AppComposition Composition, TrayMenuController Tray, FakeWorkspace Workspace,
        LayoutTree Tree, Func<int> GapReloadCalls);

    private static Harness Wire(
        Func<int> loadGap, bool tilingEnabled = true,
        Action<Action>? scheduleOnOwningThread = null,
        CosmicWin.App.Diagnostics.IDesktopTrace? desktopTrace = null)
    {
        var workspace = new FakeWorkspace();
        var primary = new FakeDisplay(
            new IntPtr(1), Rectangle.FromSize(0, 0, 1920, 1080), Rectangle.FromSize(0, 0, 1920, 1080), 1.0, true);
        var registry = new WindowRegistry();
        var treeManager = new TreeManager([primary], primary, registry);
        var gapReloadCalls = 0;
        TrayMenuController? tray = null;

        var composition = AppComposition.Wire(
            workspace, treeManager, registry, new Foreground(), new ExceptionListStore(ExceptionList.Empty),
            focusTrace: new RecordingFocusTrace(),
            disableTaskTrigger: () => { },
            scheduleReconcile: new NeverFiringReconcileScheduler().Schedule,
            hookFactory: writer => new LowLevelKeyboardHook(
                writer, new FakeKeyboardHookPlatform(), TimeSpan.FromSeconds(5), () => 0),
            loadExceptions: () => ExceptionList.Empty,
            shutdown: () => { },
            buildTray: controller =>
            {
                tray = controller;
                return new NullDisposable();
            },
            importVideoWallpaper: path => path,
            tilingEnabled: tilingEnabled,
            scheduleOnOwningThread: scheduleOnOwningThread,
            desktopTrace: desktopTrace,
            loadGap: () =>
            {
                gapReloadCalls++;
                return loadGap();
            });

        treeManager.TryGetTree(primary, out var tree);
        return new Harness(composition, tray!, workspace, tree!, () => gapReloadCalls);
    }

    /// <summary>
    /// Reused, not reinvented: the exact edge/between-tile arithmetic
    /// <c>TreeArrangerGapTests.TwoTiles_LeaveTheSameGapAtEveryEdgeAndBetweenThem</c> already proves
    /// for a single filling tile -- left/top edge equal the gap, right/bottom edge are the display
    /// size minus the gap.
    /// </summary>
    private static Rectangle FillingAt(int gap) =>
        new(gap, gap, 1920 - gap, 1080 - gap);

    /// <summary>
    /// <c>TreeArranger.Gap</c> is a shared mutable static (see its own remarks on why that is
    /// normally risky); set and restored here explicitly, rather than trusting whatever ambient
    /// value an earlier test left behind -- this
    /// assembly's static field starts at the CLR default (0), not <see cref="TreeArranger.DefaultGap"/>.
    /// </summary>
    [Fact]
    public void Reload_AppliesTheNewGapAndRearrangesTiledWindows()
    {
        var originalGap = TreeArranger.Gap;
        TreeArranger.Gap = 8;
        try
        {
            var gap = 8;
            var harness = Wire(() => gap);
            using (harness.Composition)
            {
                var window = new RecordingWindow(new IntPtr(5001), Rectangle.FromSize(0, 0, 800, 600));
                harness.Workspace.RaiseWindowAdded(window);
                Assert.Equal(FillingAt(8), window.Bounds);

                gap = 24;
                harness.Tray.Reload();

                Assert.Equal(24, TreeArranger.Gap);
                Assert.Equal(FillingAt(24), window.Bounds);
            }
        }
        finally
        {
            TreeArranger.Gap = originalGap;
        }
    }

    /// <summary>
    /// Mirrors <c>ToggleTiling</c>'s OFF-branch invariant (<see
    /// cref="TilingModeTests.WithTilingOff_ADraggedWindowIsNoLongerSnappedBack"/>): while tiling is
    /// off, every window is deliberately left exactly where the layout last put it. A live gap
    /// change must still update <c>TreeArranger.Gap</c> (so it is there the moment tiling resumes),
    /// but must not reach
    /// in and rearrange windows that mode promised not to touch.
    /// </summary>
    /// <remarks>
    /// The window is tiled FIRST, while tiling is still on, and only THEN is tiling switched off --
    /// not wired off from the start. A window added while tiling is already off is never given a
    /// tree leaf at all (<see cref="TilingModeTests.WithTilingOff_ANewWindowIsLeftWhereItOpened"/>),
    /// so <c>RearrangeEveryDisplay</c> would find nothing to reposition and this fact would pass
    /// whether or not the tiling guard in <c>ReloadGap</c> exists -- caught by mutation-testing this
    /// fact against a <c>ReloadGap</c> with the guard deleted: with the window added while tiling was
    /// already off, all three facts in this file stayed green; only tiling the window BEFORE turning
    /// it off, as below, makes this fact fail the way it should.
    /// </remarks>
    [Fact]
    public void ReloadWithTilingOff_UpdatesTheGapButLeavesWindowsAlone()
    {
        var originalGap = TreeArranger.Gap;
        TreeArranger.Gap = 8;
        try
        {
            var gap = 8;
            var harness = Wire(() => gap);
            using (harness.Composition)
            {
                var window = new RecordingWindow(new IntPtr(5002), Rectangle.FromSize(0, 0, 800, 600));
                harness.Workspace.RaiseWindowAdded(window);
                Assert.Equal(FillingAt(8), window.Bounds);
                var tiledCalls = window.SetPositionCallCount;

                harness.Tray.ToggleTiling();

                gap = 24;
                harness.Tray.Reload();

                Assert.Equal(24, TreeArranger.Gap);
                Assert.Equal(tiledCalls, window.SetPositionCallCount);
                Assert.Equal(FillingAt(8), window.Bounds);
            }
        }
        finally
        {
            TreeArranger.Gap = originalGap;
        }
    }

    /// <summary>
    /// T13 (review R4-reload-swallow-without-trace): <c>CompositionRoot.Reload</c>'s
    /// own try/catch around <c>reloadGap?.Invoke()</c> only ever observes a SYNCHRONOUS failure. Every
    /// other fact in this file relies on <c>AppComposition.Wire</c>'s default (synchronous)
    /// <c>scheduleOnOwningThread</c>, which happens to run <c>ReloadGap</c> inline -- masking a real
    /// production bug, because production wires <c>scheduleOnOwningThread: RunOnUiThread</c>
    /// (<c>Dispatcher.BeginInvoke</c>), which returns before <c>ReloadGap</c>'s body ever runs.
    /// <c>CompositionRoot.Reload</c> has already returned successfully by the time a failure would
    /// occur, so nothing ever caught or traced it. This fact defers the queued action the way
    /// <c>Dispatcher.BeginInvoke</c> actually does (stores it instead of running it), to prove the
    /// trace fires from wherever <c>ReloadGap</c> actually executes, not from the already-returned
    /// enqueue call.
    /// </summary>
    [Fact]
    public void Reload_WithDeferredSchedulingLikeTheRealDispatcher_StillTracesAGapReloadFailure()
    {
        var trace = new RecordingDesktopTrace();
        // Every scheduled action is kept (not just the last one): Reload also queues unrelated
        // owning-thread work, so the test runs each item on its own and proves that exactly one of
        // them -- the deferred gap reload -- is what writes the trace line. The list is drained as a
        // queue, not a snapshot, so work an item schedules while it runs is run and counted too
        // (R3-gap-reload-snapshot-skips-nested-deferred-work).
        var deferredWork = new List<Action>();
        var harness = Wire(
            () => throw new InvalidOperationException("settings.conf unreadable"),
            scheduleOnOwningThread: deferredWorkItem => deferredWork.Add(deferredWorkItem),
            desktopTrace: trace);
        using (harness.Composition)
        {
            var thrownByReload = Record.Exception(() => harness.Tray.Reload());

            Assert.Null(thrownByReload);
            Assert.NotEmpty(deferredWork);
            Assert.DoesNotContain(trace.Lines, line => IsGapFailureLine(line));

            // Bounded, so an item that keeps rescheduling itself fails the test instead of hanging it.
            const int MaxDeferredItems = 100;
            var itemsThatTraced = 0;
            for (var index = 0; index < deferredWork.Count; index++)
            {
                Assert.True(index < MaxDeferredItems, "Expected the deferred work to drain, not keep rescheduling itself.");
                var deferred = deferredWork[index];
                var before = trace.Lines.Count(IsGapFailureLine);

                Assert.Null(Record.Exception(deferred));

                if (trace.Lines.Count(IsGapFailureLine) > before)
                {
                    itemsThatTraced++;
                }
            }

            Assert.Equal(1, itemsThatTraced);
        }
    }

    private static bool IsGapFailureLine(string line) =>
        line.Contains("reload-gap-failed", StringComparison.Ordinal);

    /// <summary>
    /// R4-reload-gap-swallow-depends-on-optional-trace: the deferred catch inside
    /// <c>AppComposition.ReloadGap</c> is the ONLY place that sees an asynchronous failure, so with no
    /// desktop trace it must still report to <see cref="System.Diagnostics.Trace"/>.
    /// </summary>
    [Fact]
    public void Reload_WithDeferredSchedulingAndNoDesktopTrace_StillReportsAGapReloadFailureToTheFallbackSink()
    {
        using var listener = new CosmicWin.App.Tests.TestDoubles.CapturingTraceListener();
        Action? deferred = null;
        var harness = Wire(
            () => throw new InvalidOperationException("settings.conf unreadable (deferred fallback sink)"),
            scheduleOnOwningThread: work => deferred = work);
        using (harness.Composition)
        {
            harness.Tray.Reload();
            Assert.NotNull(deferred);

            var thrown = Record.Exception(deferred!);

            Assert.Null(thrown);
            Assert.Contains(listener.Lines, line =>
                line.Contains("reload-gap-failed", StringComparison.Ordinal)
                && line.Contains("deferred fallback sink", StringComparison.Ordinal));
        }
    }

    /// <summary>Reload is the ONLY trigger -- a gap change on disk does nothing until it is asked for.</summary>
    [Fact]
    public void GapIsNotReReadOnItsOwn_OnlyOnReload()
    {
        var originalGap = TreeArranger.Gap;
        TreeArranger.Gap = 8;
        try
        {
            var gap = 8;
            var harness = Wire(() => gap);
            using (harness.Composition)
            {
                gap = 24;

                Assert.Equal(8, TreeArranger.Gap);
                Assert.Equal(0, harness.GapReloadCalls());

                harness.Tray.Reload();

                Assert.Equal(24, TreeArranger.Gap);
                Assert.Equal(1, harness.GapReloadCalls());
            }
        }
        finally
        {
            TreeArranger.Gap = originalGap;
        }
    }
}
