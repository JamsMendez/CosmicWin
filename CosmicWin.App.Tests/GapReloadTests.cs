using CosmicWin.App.Input;
using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.App.Tray;
using CosmicWin.Interop;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App.Tests;

/// <summary>
/// T5 (alert-tile-mosaic, maintainer decision 2026-09-26): the new <c>gap</c> settings key, driven
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
    /// T11 (alert-tile-mosaic, review R3-immediate-scheduler-never-fires): renamed from
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

    private sealed record Harness(
        AppComposition Composition, TrayMenuController Tray, FakeWorkspace Workspace,
        LayoutTree Tree, Func<int> GapReloadCalls);

    private static Harness Wire(Func<int> loadGap, bool tilingEnabled = true)
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
    /// normally risky); set and restored here explicitly, the same convention <see
    /// cref="Alerts.WebViewAlertCompositionWiringTests.MultiGroupCommand_ThreadsTheFullTileListGridAndGapToTheLayer"/>
    /// uses, rather than trusting whatever ambient value an earlier test left behind -- this
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
    /// change must still update <c>TreeArranger.Gap</c> (so it is there the moment tiling resumes,
    /// and it already reaches the alert mosaic regardless of the tiling switch), but must not reach
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
