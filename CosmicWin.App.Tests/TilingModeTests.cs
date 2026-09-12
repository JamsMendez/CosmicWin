using CosmicWin.App.Input;
using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.App.Tray;
using CosmicWin.Interop;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App.Tests;

/// <summary>
/// The tray's tiling switch, driven END TO END through the composition the app really runs.
/// </summary>
/// <remarks>
/// <para>
/// Written at this level for the same reason <see cref="AppCompositionTests"/> is: the switch is a
/// WIRING decision -- one flag read by the executor, by the window adapter and by the dialog
/// adapter -- and a unit test of any one of them would pass on a composition that forgot the other
/// two. What the user asked for is a mode where the desktop chords still work, and only the whole
/// pipeline can be asked whether they do.
/// </para>
/// <para>
/// Deliberately NOT a copy of the pause facts. Pausing stops everything, which is the thing this
/// mode exists to be different from, so every fact here pairs "the layout is left alone" with
/// "the desktop chord still lands".
/// </para>
/// </remarks>
public sealed class TilingModeTests
{
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

    private sealed class NullDisposable : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class MutableForeground : IForegroundWindowSource
    {
        public nint Handle { get; set; }

        public nint GetForegroundHandle() => Handle;
    }

    private sealed class MutableVirtualDesktops : IVirtualDesktopService
    {
        public bool IsSupported => true;

        public int Count => 2;

        public int CurrentIndex => 1;

        public Guid CurrentDesktopId => Guid.Empty;

        public string? LastError => null;

        public List<int> Switched { get; } = [];

        public List<(nint Handle, int Index)> Moved { get; } = [];

        public bool TrySwitchTo(int oneBasedIndex)
        {
            Switched.Add(oneBasedIndex);
            return true;
        }

        public bool TryMoveWindowTo(nint windowHandle, int oneBasedIndex)
        {
            Moved.Add((windowHandle, oneBasedIndex));
            return true;
        }
    }

    private sealed record Harness(
        AppComposition Composition, TrayMenuController Tray, FakeWorkspace Workspace,
        LayoutTree Tree, TreeManager TreeManager, IDisplay Primary, MutableForeground Foreground,
        MutableVirtualDesktops Desktops, FakeKeyboardHookPlatform Platform,
        LowLevelKeyboardHook Hook, List<bool> Persisted);

    private static Harness Wire(bool tilingEnabled = true)
    {
        var workspace = new FakeWorkspace();
        var primary = new FakeDisplay(
            new IntPtr(1), Rectangle.FromSize(0, 0, 1920, 1080), Rectangle.FromSize(0, 0, 1920, 1080), 1.0, true);
        var registry = new WindowRegistry();
        var treeManager = new TreeManager([primary], primary, registry);
        var foreground = new MutableForeground();
        var desktops = new MutableVirtualDesktops();
        var platform = new FakeKeyboardHookPlatform();
        var persisted = new List<bool>();
        TrayMenuController? tray = null;
        LowLevelKeyboardHook? hook = null;

        var composition = AppComposition.Wire(
            workspace, treeManager, registry, foreground, new ExceptionListStore(ExceptionList.Empty),
            focusTrace: new RecordingFocusTrace(),
            disableTaskTrigger: () => { },
            scheduleReconcile: new ImmediateScheduler().Schedule,
            hookFactory: writer =>
            {
                hook = new LowLevelKeyboardHook(writer, platform, TimeSpan.FromSeconds(5), () => 0);
                return hook;
            },
            loadExceptions: () => ExceptionList.Empty,
            shutdown: () => { },
            buildTray: controller =>
            {
                tray = controller;
                return new NullDisposable();
            },
            virtualDesktops: desktops,
            tilingEnabled: tilingEnabled,
            persistTiling: persisted.Add);

        treeManager.TryGetTree(primary, out var tree);
        return new Harness(
            composition, tray!, workspace, tree!, treeManager, primary, foreground, desktops,
            platform, hook!, persisted);
    }

    /// <summary>The switch starts where the settings file left it, not where the code guesses.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheTrayReportsTheStoredSetting(bool stored)
    {
        var harness = Wire(tilingEnabled: stored);
        using (harness.Composition)
        {
            Assert.Equal(stored, harness.Tray.IsTilingEnabled);
        }
    }

    /// <summary>
    /// The core of it: with tiling off a new window is left exactly where it opened. It is not
    /// tiled, and -- unlike a window CosmicWin gave up on -- nothing moved it first.
    /// </summary>
    [Fact]
    public void WithTilingOff_ANewWindowIsLeftWhereItOpened()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            harness.Tray.ToggleTiling();

            var window = new RecordingWindow(new IntPtr(1001), Rectangle.FromSize(300, 200, 800, 600));
            harness.Workspace.RaiseWindowAdded(window);

            Assert.Null(harness.Tree.Root);
            Assert.Equal(0, window.SetPositionCallCount);
            Assert.Equal(Rectangle.FromSize(300, 200, 800, 600), window.Bounds);
        }
    }

    /// <summary>
    /// A window already tiled when the switch goes off keeps its place and is then left alone: a
    /// drag is no longer snapped back, which is the freedom the mode is FOR.
    /// </summary>
    [Fact]
    public void WithTilingOff_ADraggedWindowIsNoLongerSnappedBack()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            var window = new RecordingWindow(new IntPtr(1002), Rectangle.FromSize(0, 0, 800, 600));
            harness.Workspace.RaiseWindowAdded(window);
            var tiledCalls = window.SetPositionCallCount;

            harness.Tray.ToggleTiling();

            window.SimulateExternalMove(Rectangle.FromSize(640, 360, 400, 300));
            harness.Workspace.RaiseWindowBoundsChanged(window);

            Assert.Equal(tiledCalls, window.SetPositionCallCount);
            Assert.Equal(Rectangle.FromSize(640, 360, 400, 300), window.Bounds);
        }
    }

    /// <summary>
    /// The whole point of the switch existing beside Pausar: the desktop chords are still answered.
    /// Driven from the real keyboard hook so the gate cannot be satisfied by an executor that is
    /// simply never reached.
    /// </summary>
    [Fact]
    public async Task WithTilingOff_TheDesktopChordsStillReachTheShell()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            harness.Tray.ToggleTiling();

            Assert.True(harness.Platform.Raise(KeyboardKey.D2, isKeyDown: true, ModifierKeys.Alt));
            Assert.True(await WaitUntil(() => harness.Desktops.Switched.Contains(2)));

            Assert.True(harness.Platform.Raise(
                KeyboardKey.D3, isKeyDown: true, ModifierKeys.Shift | ModifierKeys.Alt));
            Assert.True(await WaitUntil(() => harness.Desktops.Moved.Any(move => move.Index == 3)));
        }
    }

    /// <summary>
    /// And the other side of the same run: the layout chords are dropped.
    /// </summary>
    /// <remarks>
    /// A DESKTOP chord is pressed straight after the layout one and waited for, rather than waiting
    /// out a timeout on the layout chord itself. The dispatcher is a single-reader FIFO channel, so
    /// the second chord landing proves the first was already drained -- which is the difference
    /// between "it did nothing" and "it had not run yet", and the whole reason a negative assertion
    /// on an async pipeline is usually worthless.
    /// </remarks>
    [Fact]
    public async Task WithTilingOff_ALayoutChordChangesNothing()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            var left = new RecordingWindow(new IntPtr(2001), Rectangle.FromSize(0, 0, 960, 1080));
            var right = new RecordingWindow(new IntPtr(2002), Rectangle.FromSize(960, 0, 960, 1080));
            harness.Workspace.RaiseWindowAdded(left);
            harness.Workspace.RaiseWindowAdded(right);
            harness.Foreground.Handle = left.Handle;

            harness.Tray.ToggleTiling();
            var placements = left.SetPositionCallCount + right.SetPositionCallCount;

            Assert.True(harness.Platform.Raise(KeyboardKey.L, isKeyDown: true, ModifierKeys.Alt));
            Assert.True(harness.Platform.Raise(KeyboardKey.D2, isKeyDown: true, ModifierKeys.Alt));
            Assert.True(await WaitUntil(() => harness.Desktops.Switched.Contains(2)));

            Assert.Equal(0, right.TryActivateCallCount);
            Assert.Equal(placements, left.SetPositionCallCount + right.SetPositionCallCount);
        }
    }

    /// <summary>
    /// The resize chord survives the switch, end to end: a real Ctrl+Alt+L through the hook, the
    /// dispatcher and the executor, landing on the foreground window's own rectangle.
    /// </summary>
    /// <remarks>
    /// The rule is the tiled one read against the work area -- grow into the pressed side while
    /// there is room -- so a window with 1120px to its right grows by 5% of 1920, and its left edge
    /// does not move.
    /// </remarks>
    [Fact]
    public async Task WithTilingOff_TheResizeChordGrowsTheForegroundWindow()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            // Opened AFTER the switch went off, so it keeps the rectangle its application chose.
            // Added first, it would have been tiled to the whole work area and the fact would be
            // measuring a tile rather than a window.
            harness.Tray.ToggleTiling();

            var window = new RecordingWindow(new IntPtr(2101), Rectangle.FromSize(100, 100, 800, 600));
            harness.Workspace.RaiseWindowAdded(window);
            harness.Foreground.Handle = window.Handle;
            Assert.Equal(0, window.SetPositionCallCount);

            Assert.True(harness.Platform.Raise(
                KeyboardKey.L, isKeyDown: true, ModifierKeys.Control | ModifierKeys.Alt));
            Assert.True(await WaitUntil(() => window.Bounds.Width != 800));

            Assert.Equal(Rectangle.FromSize(100, 100, 896, 600), window.Bounds);
        }
    }

    /// <summary>
    /// And it is never pointed at shell chrome. The taskbar is the foreground window the moment it
    /// is clicked, and it is visible and unowned -- so nothing but the exclusion rule stands
    /// between Ctrl+Alt+L and a resized taskbar.
    /// </summary>
    [Fact]
    public async Task WithTilingOff_TheResizeChordNeverTouchesShellChrome()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            harness.Tray.ToggleTiling();

            var taskbar = new RecordingWindow(
                new IntPtr(2102), Rectangle.FromSize(0, 1032, 1920, 48),
                className: "Shell_TrayWnd", exStyle: WindowStyleFlags.ExToolWindow);
            harness.Workspace.RaiseWindowAdded(taskbar);
            harness.Foreground.Handle = taskbar.Handle;

            Assert.True(harness.Platform.Raise(
                KeyboardKey.L, isKeyDown: true, ModifierKeys.Control | ModifierKeys.Alt));

            // A DESKTOP chord straight after, waited for: the dispatcher is a single-reader FIFO,
            // so the second landing proves the first was already drained. Without it this asserts
            // on a chord that simply had not run yet.
            Assert.True(harness.Platform.Raise(KeyboardKey.D2, isKeyDown: true, ModifierKeys.Alt));
            Assert.True(await WaitUntil(() => harness.Desktops.Switched.Contains(2)));

            Assert.Equal(0, taskbar.SetPositionCallCount);
        }
    }

    /// <summary>
    /// Turning it back on is a request to put the layout back -- including for the windows that
    /// opened while it was off, which the workspace will never announce a second time on its own.
    /// </summary>
    [Fact]
    public void TurningTilingBackOn_AdoptsTheWindowsThatOpenedWhileItWasOff()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            harness.Tray.ToggleTiling();

            var window = new RecordingWindow(new IntPtr(3001), Rectangle.FromSize(300, 200, 800, 600));
            harness.Workspace.RaiseWindowAdded(window);
            Assert.Null(harness.Tree.Root);

            harness.Tray.ToggleTiling();

            var leaf = Assert.IsType<LeafNode>(harness.Tree.Root);
            Assert.Equal(new WindowRef(window.Handle), leaf.Window);
            Assert.Equal(Rectangle.FromSize(0, 0, 1920, 1080), window.Bounds);
        }
    }

    /// <summary>
    /// A window that stayed in the tree is put back where the tree says it belongs, undoing every
    /// drag the user made while the layout was off duty.
    /// </summary>
    [Fact]
    public void TurningTilingBackOn_PutsADraggedWindowBackInItsSlot()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            var window = new RecordingWindow(new IntPtr(3002), Rectangle.FromSize(0, 0, 800, 600));
            harness.Workspace.RaiseWindowAdded(window);

            harness.Tray.ToggleTiling();
            window.SimulateExternalMove(Rectangle.FromSize(640, 360, 400, 300));
            harness.Tray.ToggleTiling();

            Assert.Equal(Rectangle.FromSize(0, 0, 1920, 1080), window.Bounds);
        }
    }

    /// <summary>
    /// Alt+T flips the mode from the keyboard, driven through the real hook so the chord cannot be
    /// satisfied by an executor that is simply never reached -- and it persists like a tray click,
    /// because it is the SAME switch and not a second copy of one.
    /// </summary>
    [Fact]
    public async Task TheTilingChord_TurnsTheModeOff()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            Assert.True(harness.Platform.Raise(KeyboardKey.T, isKeyDown: true, ModifierKeys.Alt));

            Assert.True(await WaitUntil(() => !harness.Tray.IsTilingEnabled));
            Assert.Equal([false], harness.Persisted);
        }
    }

    /// <summary>
    /// The half that actually matters: with the layout already off, Alt+T puts it BACK.
    /// </summary>
    /// <remarks>
    /// This chord is answered above the executor's layout gate, unlike every chord that reaches the
    /// tree. Below it, the only thing the keyboard could ever do to this mode is switch it off --
    /// a one-way door out, with the tray the sole way home.
    /// </remarks>
    [Fact]
    public async Task TheTilingChord_TurnsTheModeBackOn_AndPutsTheLayoutBack()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            var window = new RecordingWindow(new IntPtr(4001), Rectangle.FromSize(0, 0, 800, 600));
            harness.Workspace.RaiseWindowAdded(window);

            harness.Tray.ToggleTiling();
            window.SimulateExternalMove(Rectangle.FromSize(640, 360, 400, 300));

            Assert.True(harness.Platform.Raise(KeyboardKey.T, isKeyDown: true, ModifierKeys.Alt));

            Assert.True(await WaitUntil(() => harness.Tray.IsTilingEnabled));
            Assert.True(await WaitUntil(() => window.Bounds.Width == 1920));
            Assert.Equal(Rectangle.FromSize(0, 0, 1920, 1080), window.Bounds);
        }
    }

    /// <summary>
    /// Persisted on every flip, so the mode survives a restart the way the border already does.
    /// </summary>
    [Fact]
    public void EveryFlipIsPersisted()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            harness.Tray.ToggleTiling();
            harness.Tray.ToggleTiling();

            Assert.Equal([false, true], harness.Persisted);
        }
    }

    /// <summary>
    /// The two switches stay independent at the COMPOSITION, not just in the controller: turning
    /// tiling off must not pause the hook, or the desktop chords it is supposed to preserve would
    /// die with it.
    /// </summary>
    [Fact]
    public void TurningTilingOff_DoesNotPauseTheHook()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            harness.Tray.ToggleTiling();

            Assert.False(harness.Hook.IsPaused);
            Assert.False(harness.Tray.IsPaused);
        }
    }

    private static async Task<bool> WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        return condition();
    }
}
