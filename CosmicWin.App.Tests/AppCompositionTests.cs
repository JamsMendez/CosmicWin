using System.Threading.Channels;
using CosmicWin.App.Diagnostics;
using CosmicWin.App.Input;
using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.App.Tray;
using CosmicWin.Interop;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App.Tests;

/// <summary>
/// Closes verify-report #21 revision 15 SUGGESTION V15-S1 and re-attacks carried WARNING V14-W1.
/// <c>App.xaml.cs</c>'s composition (four consecutive closures -- V11-W1, V12-W1, V13-W1, V14-W1 --
/// each defeated a mutation the previous one did not anticipate, the last one falling to three
/// probes editing only <c>App.xaml.cs</c>) is extracted here into <see cref="AppComposition"/>, a
/// plain class with no WPF base type. Unlike the deleted <c>CompositionSiteArchitectureTests</c>
/// (superseded -- see its narrower replacement, <c>AppEntryPointThinnessTests</c>), these facts
/// drive <see cref="AppComposition.Wire"/> END TO END with a real <see
/// cref="LowLevelKeyboardHook"/> (via <see cref="FakeKeyboardHookPlatform"/>, the same seam
/// <c>CompositionRootTests</c> already established) and a real <see cref="TrayMenuController"/>
/// captured through the injected <c>buildTray</c> factory -- so a mis-wiring is now an assertable
/// RUNTIME BEHAVIOR, not a source-text spelling question. All collaborators (<c>tree</c>,
/// <c>hook</c>) stay owned by the TEST via constructor injection / factory capture, matching the
/// existing <c>CompositionRootTests</c> idiom -- no extra public surface is added to <see
/// cref="AppComposition"/> beyond what <c>App.xaml.cs</c> itself needs (<see
/// cref="AppComposition.WireProduction"/> and <see cref="AppComposition.Dispose"/>).
/// </summary>
public sealed class AppCompositionTests
{
    private sealed record Harness(
        AppComposition Composition, TrayMenuController TrayController, LowLevelKeyboardHook Hook,
        FakeWorkspace Workspace, LayoutTree Tree, TreeManager TreeManager, IDisplay Primary, IDisplay Secondary,
        RecordingFocusTrace FocusTrace, MutableForegroundWindowSource Foreground,
        FakeKeyboardHookPlatform Platform, RecordingScheduler Scheduler);

    /// <summary>
    /// Captures the recurring reconciliation pass instead of running a real timer, so a test can
    /// fire it deterministically and observe disposal.
    /// </summary>
    private sealed class RecordingScheduler
    {
        public TimeSpan? Interval { get; private set; }

        public Action? Callback { get; private set; }

        public int DisposeCallCount { get; private set; }

        public IDisposable Schedule(TimeSpan interval, Action callback)
        {
            Interval = interval;
            Callback = callback;
            return new Stopper(this);
        }

        public void Fire() => Callback!();

        private sealed class Stopper(RecordingScheduler owner) : IDisposable
        {
            public void Dispose() => owner.DisposeCallCount++;
        }
    }

    private static FakeDisplay Display(int handle, int left, int top, int width, int height, bool primary = false) =>
        new(new IntPtr(handle), Rectangle.FromSize(left, top, width, height),
            Rectangle.FromSize(left, top, width, height), 1.0, primary);

    private static Harness WireHarness(Action? shutdown = null, Action? disableTaskTrigger = null)
    {
        var workspace = new FakeWorkspace();
        var primary = Display(1, 0, 0, 1920, 1080, primary: true);
        var secondary = Display(2, 1920, 0, 1280, 720);
        var registry = new WindowRegistry();
        var treeManager = new TreeManager(new IDisplay[] { primary, secondary }, primary, registry);
        var foreground = new MutableForegroundWindowSource();
        var exceptionStore = new ExceptionListStore(ExceptionList.Empty);
        var platform = new FakeKeyboardHookPlatform();
        var focusTrace = new RecordingFocusTrace();
        var scheduler = new RecordingScheduler();
        LowLevelKeyboardHook? capturedHook = null;
        TrayMenuController? capturedController = null;

        var composition = AppComposition.Wire(
            workspace, treeManager, registry, foreground, exceptionStore,
            focusTrace: focusTrace,
            disableTaskTrigger: disableTaskTrigger ?? (() => { }),
            scheduleReconcile: scheduler.Schedule,
            hookFactory: writer =>
            {
                capturedHook = new LowLevelKeyboardHook(writer, platform, TimeSpan.FromSeconds(5), () => 0);
                return capturedHook;
            },
            loadExceptions: () => ExceptionList.Empty,
            shutdown: shutdown ?? (() => { }),
            buildTray: controller =>
            {
                capturedController = controller;
                return new DisposeCountingTray();
            });

        treeManager.TryGetTree(primary, out var tree);
        return new Harness(
            composition, capturedController!, capturedHook!, workspace, tree!, treeManager, primary, secondary,
            focusTrace, foreground, platform, scheduler);
    }

    /// <summary>Baseline sanity: unpaused, a newly-added window IS tracked and arranged -- proves <see cref="AppComposition.Wire"/> genuinely wires the adapter, not a no-op stub.</summary>
    [Fact]
    public void Wire_Unpaused_NewWindow_IsAddedToTreeAndArranged()
    {
        var harness = WireHarness();
        using (harness.Composition)
        {
            var window = new RecordingWindow(new IntPtr(1001), Rectangle.FromSize(0, 0, 800, 600));
            harness.Workspace.RaiseWindowAdded(window);

            var leaf = Assert.IsType<LeafNode>(harness.Tree.Root);
            Assert.Equal(new WindowRef(window.Handle), leaf.Window);
            Assert.Equal(1, window.SetPositionCallCount);
        }
    }

    /// <summary>WU17 (closes W3): proves <see cref="AppComposition.Wire"/> genuinely routes through the given <see cref="TreeManager"/> -- a window on the secondary display lands in the secondary's own tree, not the primary's.</summary>
    [Fact]
    public void Wire_TwoDisplays_RoutesEachWindowToItsOwnMonitorTree()
    {
        var harness = WireHarness();
        using (harness.Composition)
        {
            var onPrimary = new RecordingWindow(new IntPtr(2001), Rectangle.FromSize(100, 100, 400, 300));
            var onSecondary = new RecordingWindow(new IntPtr(2002), Rectangle.FromSize(2000, 100, 400, 300));
            harness.Workspace.RaiseWindowAdded(onPrimary);
            harness.Workspace.RaiseWindowAdded(onSecondary);

            harness.TreeManager.TryGetTree(harness.Primary, out var primaryTree);
            harness.TreeManager.TryGetTree(harness.Secondary, out var secondaryTree);
            var primaryLeaf = Assert.IsType<LeafNode>(primaryTree!.Root);
            var secondaryLeaf = Assert.IsType<LeafNode>(secondaryTree!.Root);
            Assert.Equal(new WindowRef(onPrimary.Handle), primaryLeaf.Window);
            Assert.Equal(new WindowRef(onSecondary.Handle), secondaryLeaf.Window);
        }
    }

    /// <summary>
    /// The P1/P6-killer: toggling pause through the SAME <see cref="TrayMenuController"/> <see
    /// cref="AppComposition.Wire"/> composed must gate the SAME hook the adapter's gate delegate
    /// reads. If the composition ever rebinds the local hook variable between the sanctioned
    /// adapter construction and the point the tray controller is wired (verify-report #21 probe
    /// P1's exact shape), the tray toggle affects the NEW hook while the adapter's gate keeps
    /// reading the orphaned OLD one -- this fact goes RED the instant that happens, because it
    /// exercises real runtime behavior, not source text.
    /// </summary>
    [Fact]
    public void Wire_TogglePauseThroughComposedTrayController_BlocksSubsequentWindowAdd()
    {
        var harness = WireHarness();
        using (harness.Composition)
        {
            Assert.False(harness.TrayController.IsPaused);
            var next = harness.TrayController.TogglePause();
            Assert.True(next);

            // The SAME hook object the tray toggled must be the one the adapter's gate reads --
            // this is the specific fact a hook rebind between construction and wiring (probe P1's
            // shape) would break, even though both sides still individually report "paused".
            Assert.True(harness.Hook.IsPaused);

            var window = new RecordingWindow(new IntPtr(1002), Rectangle.FromSize(0, 0, 800, 600));
            harness.Workspace.RaiseWindowAdded(window);

            Assert.Null(harness.Tree.Root);
            Assert.Equal(0, window.SetPositionCallCount);
        }
    }

    /// <summary><see cref="AppComposition.Wire"/> genuinely opens the injected workspace -- approval test for the exact <c>OnStartup</c> ordering it replaces (Build -&gt; hook -&gt; adapter -&gt; workspace.Open() -&gt; hook.Start() -&gt; tray).</summary>
    [Fact]
    public void Wire_OpensTheInjectedWorkspace()
    {
        var harness = WireHarness();
        using (harness.Composition)
        {
            Assert.True(harness.Workspace.IsOpen);
        }
    }

    /// <summary>
    /// MR-2 (Engram discovery #101): the focus diagnostic is worthless if it is not actually wired
    /// into the composition the app really runs, so this drives the FULL production chain -- a real
    /// <see cref="LowLevelKeyboardHook"/> raising <c>Alt+L</c>, the dispatcher loop, the executor's
    /// tree walk, activation -- and asserts the recorded entry. Reading the LAST entry rather than
    /// the only one is deliberate: <see cref="FakeKeyboardHookPlatform"/> fires an <c>Alt+H</c> at
    /// install time, which legitimately records an earlier unresolved-focus entry.
    /// </summary>
    [Fact]
    public async Task Wire_FocusChord_RecordsTheWalkAndActivationThroughTheComposedPipeline()
    {
        var harness = WireHarness();
        using (harness.Composition)
        {
            var left = new RecordingWindow(new IntPtr(3001), Rectangle.FromSize(0, 0, 960, 1080));
            var right = new RecordingWindow(new IntPtr(3002), Rectangle.FromSize(960, 0, 960, 1080));
            harness.Workspace.RaiseWindowAdded(left);
            harness.Workspace.RaiseWindowAdded(right);
            harness.Foreground.Handle = left.Handle;

            Assert.True(harness.Platform.Raise(KeyboardKey.L, isKeyDown: true, ModifierKeys.Alt));

            var recorded = await WaitUntil(
                () => harness.FocusTrace.Entries.Any(entry => entry.Outcome == FocusTraceOutcome.Activated),
                TimeSpan.FromSeconds(2));

            Assert.True(recorded);
            var entry = harness.FocusTrace.Entries[^1];
            Assert.Equal(Direction.Right, entry.Direction);
            Assert.Equal(left.Handle, entry.FocusedHandle);
            Assert.Equal(right.Handle, entry.TargetHandle);
            Assert.Equal(FocusTraceOutcome.Activated, entry.Outcome);
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
    /// TC-3-W1: Salir must stop the logon trigger from bringing CosmicWin back on the next sign-in,
    /// and it must do so BEFORE the process is torn down -- after <c>shutdown</c> there is no
    /// guarantee anything still runs. The order assertion is the whole point; asserting only that
    /// both ran would pass on a composition that disables nothing until it is already exiting.
    /// </summary>
    [Fact]
    public void Wire_TrayExit_DisablesTheScheduledTaskTrigger_BeforeShuttingDown()
    {
        var order = new List<string>();
        var harness = WireHarness(
            shutdown: () => order.Add("shutdown"),
            disableTaskTrigger: () => order.Add("disable"));

        using (harness.Composition)
        {
            harness.TrayController.Exit();
        }

        Assert.Equal(new[] { "disable", "shutdown" }, order);
    }

    /// <summary>TC-3's other two menu entries must stay untouched: only Salir disables the trigger.</summary>
    [Fact]
    public void Wire_PauseAndReload_DoNotTouchTheScheduledTaskTrigger()
    {
        var disableCount = 0;
        var harness = WireHarness(disableTaskTrigger: () => disableCount++);

        using (harness.Composition)
        {
            harness.TrayController.TogglePause();
            harness.TrayController.TogglePause();
            harness.TrayController.Reload();
        }

        Assert.Equal(0, disableCount);
    }


    /// <summary>
    /// WT-1's second scenario -- "GIVEN SetWinEventHook fails to deliver a destroy event, WHEN the
    /// next polling pass runs, THEN the stale window is detected as gone" -- had no production
    /// driver at all: <c>Win32Workspace.Poll</c> existed, was unit-tested, and was called from
    /// nowhere. The reconciliation logic was never the gap; scheduling it was. This is not
    /// hypothetical: the hook demonstrably drops windows (an EVENT_OBJECT_CREATE arriving before the
    /// window is visible was exactly how a newly opened terminal went untiled).
    /// </summary>
    [Fact]
    public void Wire_SchedulesTheReconciliationPass_OnABoundedInterval()
    {
        var harness = WireHarness();
        using (harness.Composition)
        {
            Assert.NotNull(harness.Scheduler.Interval);
            Assert.True(harness.Scheduler.Interval > TimeSpan.Zero, "The interval must be positive.");
            Assert.True(
                harness.Scheduler.Interval <= TimeSpan.FromSeconds(30),
                "WT-1 says a BOUNDED interval; a pass that rare is not a fallback.");
        }
    }

    [Fact]
    public void Wire_ReconciliationPass_DrivesTheWorkspacesOwnCatchUp()
    {
        var harness = WireHarness();
        using (harness.Composition)
        {
            Assert.Equal(0, harness.Workspace.PollCallCount);

            harness.Scheduler.Fire();
            harness.Scheduler.Fire();

            Assert.Equal(2, harness.Workspace.PollCallCount);
        }
    }

    /// <summary>A recurring pass that outlives the composition would reconcile a torn-down tree.</summary>
    [Fact]
    public void Dispose_StopsTheReconciliationPass()
    {
        var harness = WireHarness();

        harness.Composition.Dispose();

        Assert.Equal(1, harness.Scheduler.DisposeCallCount);
    }

    /// <summary>Approval test for <c>App.OnExit</c>'s exact disposal chain, now owned by <see cref="AppComposition.Dispose"/>: the injected workspace and tray both get disposed exactly once.</summary>
    [Fact]
    public void Dispose_DisposesTrayAndWorkspace_ExactlyOnce()
    {
        var workspace = new DisposeCountingWorkspace();
        var primary = Display(1, 0, 0, 1920, 1080, primary: true);
        var registry = new WindowRegistry();
        var treeManager = new TreeManager(new IDisplay[] { primary }, primary, registry);
        var foreground = new StaticForegroundWindowSource(IntPtr.Zero);
        var exceptionStore = new ExceptionListStore(ExceptionList.Empty);
        var platform = new FakeKeyboardHookPlatform();
        DisposeCountingTray? capturedTray = null;

        var composition = AppComposition.Wire(
            workspace, treeManager, registry, foreground, exceptionStore,
            focusTrace: new RecordingFocusTrace(),
            disableTaskTrigger: () => { },
            scheduleReconcile: (_, _) => new DisposeCountingTray(),
            hookFactory: writer => new LowLevelKeyboardHook(writer, platform, TimeSpan.FromSeconds(5), () => 0),
            loadExceptions: () => ExceptionList.Empty,
            shutdown: () => { },
            buildTray: controller =>
            {
                capturedTray = new DisposeCountingTray();
                return capturedTray;
            });

        composition.Dispose();

        Assert.Equal(1, workspace.DisposeCallCount);
        Assert.Equal(1, capturedTray!.DisposeCallCount);
    }

    private sealed class DisposeCountingTray : IDisposable
    {
        public int DisposeCallCount { get; private set; }
        public void Dispose() => DisposeCallCount++;
    }

    private sealed class DisposeCountingWorkspace : IWorkspace
    {
#pragma warning disable CS0067
        public event EventHandler<WindowEventArgs>? WindowAdded;
        public event EventHandler<WindowEventArgs>? WindowRemoved;
        public event EventHandler<WindowEventArgs>? WindowBoundsChanged;
#pragma warning restore CS0067
        public bool IsOpen { get; private set; }
        public IReadOnlyList<IWindow> Snapshot => Array.Empty<IWindow>();
        public int DisposeCallCount { get; private set; }
        public void Open() => IsOpen = true;
        public void Poll() { }
        public void Dispose() => DisposeCallCount++;
    }

    private sealed class StaticForegroundWindowSource(nint handle) : IForegroundWindowSource
    {
        public nint GetForegroundHandle() => handle;
    }

    private sealed class MutableForegroundWindowSource : IForegroundWindowSource
    {
        public nint Handle { get; set; }

        public nint GetForegroundHandle() => Handle;
    }
}
