using System.Threading.Channels;
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
        FakeWorkspace Workspace, LayoutTree Tree, TreeManager TreeManager, IDisplay Primary, IDisplay Secondary);

    private static FakeDisplay Display(int handle, int left, int top, int width, int height, bool primary = false) =>
        new(new IntPtr(handle), Rectangle.FromSize(left, top, width, height),
            Rectangle.FromSize(left, top, width, height), 1.0, primary);

    private static Harness WireHarness()
    {
        var workspace = new FakeWorkspace();
        var primary = Display(1, 0, 0, 1920, 1080, primary: true);
        var secondary = Display(2, 1920, 0, 1280, 720);
        var registry = new WindowRegistry();
        var treeManager = new TreeManager(new IDisplay[] { primary, secondary }, primary, registry);
        var foreground = new StaticForegroundWindowSource(IntPtr.Zero);
        var exceptionStore = new ExceptionListStore(ExceptionList.Empty);
        var platform = new FakeKeyboardHookPlatform();
        LowLevelKeyboardHook? capturedHook = null;
        TrayMenuController? capturedController = null;

        var composition = AppComposition.Wire(
            workspace, treeManager, registry, foreground, exceptionStore,
            hookFactory: writer =>
            {
                capturedHook = new LowLevelKeyboardHook(writer, platform, TimeSpan.FromSeconds(5), () => 0);
                return capturedHook;
            },
            loadExceptions: () => ExceptionList.Empty,
            shutdown: () => { },
            buildTray: controller =>
            {
                capturedController = controller;
                return new DisposeCountingTray();
            });

        treeManager.TryGetTree(primary, out var tree);
        return new Harness(composition, capturedController!, capturedHook!, workspace, tree!, treeManager, primary, secondary);
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
}
