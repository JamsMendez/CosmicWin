using System.Management;
using CosmicWin.App;
using CosmicWin.App.Input;
using CosmicWin.Interop;
using CosmicWin.Interop.Tests.Win32;
using CosmicWin.Interop.Win32;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App.Tests.Desktop;

/// <summary>
/// The first test in this project that drives the REAL production tiling composition
/// (<see cref="TreeManager"/>, <see cref="MultiMonitorWorkspaceAdapter"/>, <see cref="TreeArranger"/>,
/// <see cref="ActionExecutor"/>, <see cref="CompositionRoot"/>, <see cref="Win32DisplayManager"/>,
/// <see cref="Win32Window"/>/<see cref="Win32NativeWindowSource"/>) against real spawned windows and
/// asserts real, independently re-read on-screen side effects -- everything else in this project stops
/// at the computed tree/geometry. Gated identically to
/// <see cref="CosmicWin.Interop.Tests.Win32.Win32NativeWindowSourceRealWindowTests"/>: skips outside an
/// explicit interactive run (<c>COSMICWIN_RUN_DESKTOP_TESTS=1</c>).
/// </summary>
/// <remarks>
/// The spawn host is <see cref="SpawnedAlacrittyWindow"/>, not <see cref="SpawnedNotepadWindow"/>
/// -- on this project's Windows build, <c>notepad.exe</c> is a tabbed packaged app and a second
/// launch opens a TAB in the first process's window rather than a second top-level window, which
/// produced a misleading tiling-geometry failure the first time this test ran (see an earlier finding).
/// <see cref="SpawnOwn"/> asserts the one-window-per-process property directly, so a future
/// regression in the host fails loudly instead of producing another confusing geometry mismatch.
/// Safety (six hard constraints, each enforced at the cited line):
/// <para>(1) Only PIDs from Process.Start are ever touched: every handle below originates from
/// <see cref="SpawnedAlacrittyWindow.Handle"/>, itself rooted in the <c>Process.Start</c> call inside
/// <see cref="SpawnedAlacrittyWindow.Spawn"/> -- see <see cref="SpawnOwn"/>. Nothing here is ever
/// selected by window title, class, or process name against the ambient desktop.</para>
/// <para>(2) Never enumerate-and-close: the sole cleanup path is <see cref="Dispose"/> below, which
/// disposes only the <see cref="SpawnedAlacrittyWindow"/> instances this test itself created.</para>
/// <para>(3) The WM under test tracks only spawned PIDs: <see cref="OwnWindowsOnlyWorkspace"/>'s
/// <c>Open</c>/<c>Poll</c> throw (structurally unreachable -- never enumerates or hooks the real
/// desktop); the only way a window ever reaches <see cref="MultiMonitorWorkspaceAdapter"/> under test
/// is via <see cref="OwnWindowsOnlyWorkspace.RaiseAdded"/>/<see cref="OwnWindowsOnlyWorkspace.RaiseRemoved"/>,
/// called only from this file with windows this test spawned. The real, production
/// <see cref="Win32Workspace"/> (which enumerates every top-level window on the desktop) is
/// deliberately never constructed anywhere below.</para>
/// <para>(4) The session's own process tree is blacklisted before any other rule: see
/// <see cref="ResolveProtectedProcessTree"/>, called as the FIRST line of the test method, before
/// anything is spawned; every spawned PID is re-checked against it in <see cref="SpawnOwn"/>
/// (redundant with (3) on purpose).</para>
/// <para>(5) Fail fast rather than run blind: <see cref="ResolveProtectedProcessTree"/> throws if it
/// cannot resolve the current process's own ancestry, instead of returning a guess.</para>
/// <para>(6) No Scheduled Task is ever touched: nothing below references <c>TaskInstaller</c> or
/// <c>schtasks</c> in any way.</para>
/// </remarks>
[Trait("Category", "RequiresDesktop")]
[Collection(Desktop.RealDesktopCollection.Name)]
public sealed class RealDesktopTilingIntegrationTests : IDisposable
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(500);


    private readonly List<SpawnedAlacrittyWindow> _spawned = [];

    /// <summary>: every handle observed so far, used by <see cref="SpawnOwn"/> to verify the spawn host's one-window-per-process property before it is relied on.</summary>
    private readonly HashSet<nint> _seenHandles = [];

    [RequiresDesktopSessionFact]
    public void RealProductionPath_TilesRealWindows_ThroughAddFocusMoveAndClose()
    {
        // Constraints 4/5: resolve and blacklist this session's own process tree BEFORE spawning
        // anything. If this cannot be determined, the exception below propagates and NOTHING is
        // ever spawned -- this test refuses to run blind.
        var protectedPids = ResolveProtectedProcessTree();

        var nativeSource = new ActivationRecordingNativeWindowSource(new Win32NativeWindowSource());
        var displayManager = new Win32DisplayManager();
        var registry = new WindowRegistry();
        var treeManager = new TreeManager(displayManager.Displays, displayManager.Primary, registry);
        // Constraint 3: this shim, never the real Win32Workspace, is the adapter's event source.
        var workspace = new OwnWindowsOnlyWorkspace();
        using var adapter = new MultiMonitorWorkspaceAdapter(
            workspace, treeManager, registry, () => ExceptionList.Empty, () => false, () => null);

        var (spawned1, window1) = SpawnOwn(protectedPids, nativeSource);
        var display = treeManager.ResolveDisplay(window1.Bounds);
        Assert.True(treeManager.TryGetTree(display, out var tree) && tree is not null);
        var workArea = WorkAreaResolver.Resolve(display);
        var axis = LayoutTree.ChooseSplitAxis(workArea.Width, workArea.Height);

        // Environmental precondition, not a defect: on a portrait primary monitor, FocusRight/
        // MoveRight below are legitimate LE-2 no-ops (no Horizontal-axis ancestor exists), not bugs
        // in the code under test. Fails fast with this explicit reason rather than misreporting.
        Assert.True(
            workArea.Width > workArea.Height,
            "Environmental precondition, not a code defect: this suite assumes a landscape primary " +
            "monitor (LE-4 picks a Horizontal split). On a portrait monitor, FocusRight/MoveRight " +
            "below are legitimate LE-2 no-ops per spec, not defects in the code under test.");

        // Step 1: one window occupies the full WORK AREA (not the full screen) -- respects the taskbar.
        workspace.RaiseAdded(window1);
        Thread.Sleep(Settle);
        AssertTilesExactly(workArea, axis, [Read(nativeSource, window1.Handle)]);

        // Step 2: second window splits along LE-4's aspect heuristic; both tile the work area with no
        // gap and no overlap.
        var (spawned2, window2) = SpawnOwn(protectedPids, nativeSource);
        workspace.RaiseAdded(window2);
        Thread.Sleep(Settle);
        AssertTilesExactly(workArea, axis, [Read(nativeSource, window1.Handle), Read(nativeSource, window2.Handle)]);

        // Step 3: third window makes three slots; spans still sum to the work area exactly, in the
        // real left-to-right (or top-to-bottom) insertion order LE-4 produces.
        var (spawned3, window3) = SpawnOwn(protectedPids, nativeSource);
        workspace.RaiseAdded(window3);
        Thread.Sleep(Settle);
        var b1 = Read(nativeSource, window1.Handle);
        var b2 = Read(nativeSource, window2.Handle);
        var b3 = Read(nativeSource, window3.Handle);
        AssertTilesExactly(workArea, axis, [b1, b2, b3]);
        Assert.True(b1.Left < b2.Left && b2.Left < b3.Left, "Expected strict left-to-right tree/insertion order.");

        // Step 4: simulated FocusRight -- calls the SAME action path ActionDispatcher calls for a
        // dispatched chord (ActionExecutor.ScheduleAsync); never SendInput.
        //
        // This step used to seed "currently focused" through a fixed IForegroundWindowSource and
        // refuse to assert the real OS foreground at all, on the strength of an earlier finding:
        // SetForegroundWindow measured returning False from a background process, judged OS policy
        // rather than a CosmicWin defect. That premise is now FALSE. Win32NativeWindowSource.Activate
        // escalates (message-queue thread -> AttachThreadInput -> synthetic Alt taps -> retry) and
        // verifies with GetForegroundWindow instead of trusting a return value, and
        // Win32NativeWindowSourceActivationTests proves it genuinely moves the real foreground
        // between two external windows and chains back and forth. So this step now uses the REAL
        // foreground source and asserts the real outcome -- both WHICH window CosmicWin attempts to
        // activate (LE-2's risk surface) and that the desktop actually followed.
        var foregroundSource = new Win32ForegroundWindowSource();
        Assert.True(
            nativeSource.TryActivateWindow(window1.Handle),
            "Could not put the leftmost window in the real foreground to start the focus walk from.");
        Thread.Sleep(Settle);

        var (_, executor) = CompositionRoot.Build(tree!, registry, foregroundSource, workArea);
        executor.TreeManager = treeManager;
        executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.FocusRight), CancellationToken.None)
            .GetAwaiter().GetResult();
        Thread.Sleep(Settle);
        Assert.Equal(window2.Handle, nativeSource.LastActivateAttempt);
        Assert.Equal(window2.Handle, foregroundSource.GetForegroundHandle());
        Assert.True(
            Read(nativeSource, window2.Handle).Left > Read(nativeSource, window1.Handle).Left,
            "The activated window must be physically to the right, not merely the next tree sibling (LE-2's risk surface).");

        // Step 5: simulated MoveRight (LE-5) -- the two windows exchange ON-SCREEN order, and the
        // three still tile the work area exactly.
        //
        // This step used to assert that the two windows land on each other's EXACT rectangles. That
        // only ever held for an even division: LayoutTree.MoveNode swaps Sizes ALONGSIDE Children,
        // so a moved node carries its own span with it. On this 3440-wide work area the three spans
        // are 1147/1147/1146, and the exchange therefore leaves each window its own width rather
        // than adopting its neighbour's. The assertion below states what LE-5 actually guarantees.
        var beforeW2 = Read(nativeSource, window2.Handle);
        var beforeW3 = Read(nativeSource, window3.Handle);
        Assert.True(beforeW2.Left < beforeW3.Left, "Precondition: window2 starts to the left of window3.");

        // reference-implementation parity: with THREE siblings a move forks rather than swaps, so
        // passing a neighbour takes two presses -- the first pairs the two windows up inside a new
        // group, the second exchanges them within it. Both are asserted, because the pair-then-
        // exchange cycle is the behaviour, and one press alone would look like a failed swap.
        executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.MoveRight), CancellationToken.None)
            .GetAwaiter().GetResult();
        Thread.Sleep(Settle);

        var pairedW2 = Read(nativeSource, window2.Handle);
        var pairedW3 = Read(nativeSource, window3.Handle);
        Assert.True(pairedW2.Left > beforeW2.Left, "The first MoveRight must still carry the window rightward.");
        Assert.True(pairedW2.Left < pairedW3.Left, "After forking, the mover sits BEFORE the neighbour it reached.");
        AssertTilesExactly(workArea, axis, [Read(nativeSource, window1.Handle), pairedW2, pairedW3]);

        executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.MoveRight), CancellationToken.None)
            .GetAwaiter().GetResult();
        Thread.Sleep(Settle);

        var afterW2 = Read(nativeSource, window2.Handle);
        var afterW3 = Read(nativeSource, window3.Handle);
        Assert.True(afterW3.Left < afterW2.Left, "The second MoveRight must exchange the two windows' on-screen order.");
        Assert.Equal(pairedW2.Width, afterW2.Width);
        Assert.Equal(pairedW3.Width, afterW3.Width);
        AssertTilesExactly(workArea, axis, [Read(nativeSource, window1.Handle), afterW3, afterW2]);

        // Step 6: close one window -- the survivors reflow to fill the vacated space.
        spawned1.Dispose();
        _spawned.Remove(spawned1);
        workspace.RaiseRemoved(window1);
        Thread.Sleep(Settle);
        AssertTilesExactly(workArea, axis, [Read(nativeSource, window2.Handle), Read(nativeSource, window3.Handle)]);
    }

    /// <summary>
    /// That run MEASURED a real defect
    /// -- a window positioned within the first frames of its existence was silently overridden by
    /// its own initialisation, landing at <c>L=1715 T=4 W=1728 H=1391</c> when it had been asked
    /// for <c>X=1720 Y=0 W=1720 H=1392</c>, overlapping its neighbour by 5px -- and reasoned that
    /// PRODUCTION should self-heal where the test did not, because the real
    /// <see cref="Win32Workspace"/> raises <c>WindowBoundsChanged</c> from its
    /// <c>EVENT_OBJECT_LOCATIONCHANGE</c> hook and <see cref="MultiMonitorWorkspaceAdapter"/>
    /// answers it with an earlier decision's snap-back. That reasoning was never executed: this shim
    /// declared <c>WindowBoundsChanged</c> and silenced the resulting CS0067 with a pragma, so
    /// <c>OnWindowBoundsChanged</c> -- the ONLY production path that recovers a self-resizing
    /// window -- had no real-window coverage at all.
    /// <para>
    /// This test drives that exact path end to end against real spawned windows: tile two, shove
    /// one out of its slot by the measured drift through a DIRECT native
    /// <see cref="INativeWindowSource.SetWindowPosition"/> (never through the tree, exactly as the
    /// app''s own late init would), then raise the event production raises, and assert the window
    /// is back on its tile -- re-read independently from the OS, not from the engine''s cache.
    /// </para>
    /// </summary>
    [RequiresDesktopSessionFact]
    public void WindowThatResizesItselfAfterBeingTiled_IsSnappedBackIntoItsSlot()
    {
        var protectedPids = ResolveProtectedProcessTree();

        var nativeSource = new ActivationRecordingNativeWindowSource(new Win32NativeWindowSource());
        var displayManager = new Win32DisplayManager();
        var registry = new WindowRegistry();
        var treeManager = new TreeManager(displayManager.Displays, displayManager.Primary, registry);
        var workspace = new OwnWindowsOnlyWorkspace();
        using var adapter = new MultiMonitorWorkspaceAdapter(
            workspace, treeManager, registry, () => ExceptionList.Empty, () => false, () => null);

        var (_, window1) = SpawnOwn(protectedPids, nativeSource);
        var display = treeManager.ResolveDisplay(window1.Bounds);
        Assert.True(treeManager.TryGetTree(display, out var tree) && tree is not null);
        var workArea = WorkAreaResolver.Resolve(display);
        var axis = LayoutTree.ChooseSplitAxis(workArea.Width, workArea.Height);

        workspace.RaiseAdded(window1);
        var (_, window2) = SpawnOwn(protectedPids, nativeSource);
        workspace.RaiseAdded(window2);
        Thread.Sleep(Settle);

        var tiled = Read(nativeSource, window2.Handle);
        AssertTilesExactly(workArea, axis, [Read(nativeSource, window1.Handle), tiled]);

        // An earlier finding's measured drift, reproduced exactly: 5px left, 4px down, 8px wider, 1px
        // shorter. Applied through the native source DIRECTLY so the tree is never told -- which is
        // precisely the shape of the defect, an out-of-band resize the window manager did not ask for.
        var drifted = new Rectangle(tiled.Left - 5, tiled.Top + 4, tiled.Right + 3, tiled.Bottom - 1);
        Assert.True(
            nativeSource.SetWindowPosition(window2.Handle, drifted),
            "Could not shove the window out of its slot, so this test would prove nothing about snapping it back.");
        Thread.Sleep(Settle);
        Assert.NotEqual(tiled, Read(nativeSource, window2.Handle));

        // What the production Win32Workspace raises when its LOCATIONCHANGE hook sees the drift.
        workspace.RaiseBoundsChanged(window2);
        Thread.Sleep(Settle);

        Assert.Equal(tiled, Read(nativeSource, window2.Handle));
        AssertTilesExactly(workArea, axis, [Read(nativeSource, window1.Handle), Read(nativeSource, window2.Handle)]);
    }

    public void Dispose()
    {
        foreach (var spawned in _spawned)
        {
            spawned.Dispose();
        }
    }

    /// <summary>Constraint 1/4: spawns via the existing hardened harness, then rejects (and disposes) any PID colliding with <paramref name="protectedPids"/> before it is ever wired into the tiling path.</summary>
    private (SpawnedAlacrittyWindow Spawned, IWindow Window) SpawnOwn(HashSet<int> protectedPids, INativeWindowSource nativeSource)
    {
        var spawned = SpawnedAlacrittyWindow.Spawn();
        if (protectedPids.Contains(spawned.ProcessId))
        {
            spawned.Dispose();
            throw new InvalidOperationException(
                "Constraint 4: a spawned window's PID collided with this session's own protected process tree -- refusing to proceed.");
        }

        // Verify the one-window-per-process property BEFORE relying on it -- a spawn host
        // that silently starts tabbing (as notepad.exe did, see an earlier finding) must fail loudly here
        // instead of producing a confusing downstream tiling-geometry mismatch.
        Assert.NotEqual(nint.Zero, spawned.Handle);
        Assert.True(
            _seenHandles.Add(spawned.Handle),
            "Spawn host produced a duplicate top-level window handle across processes -- it no " +
            "longer guarantees one top-level window per process (the exact failure mode notepad.exe " +
            "exhibited on this Windows build).");

        _spawned.Add(spawned);
        Assert.True(nativeSource.TryGetWindowInfo(spawned.Handle, out var info));
        IWindow window = new Win32Window(
            spawned.Handle, info.Title, info.Bounds, nativeSource,
            info.ClassName, info.ProcessName, info.Style, info.ExStyle, info.IsOwned);
        return (spawned, window);
    }

    /// <summary>Independent, fresh re-read of a window's real bounds -- never the tiling engine's own cached state ("observable side effect", not final state).</summary>
    private static Rectangle Read(INativeWindowSource source, nint handle)
    {
        Assert.True(source.TryGetWindowInfo(handle, out var info), "Spawned window vanished mid-test.");
        return info.Bounds;
    }

    /// <summary>Sorts by measured on-screen position (never by assumed tree index) and asserts the rects tile <paramref name="workArea"/> with zero gap and zero overlap. Zero DPI tolerance: the same zero-tolerance real Win32 round-trip already runs green in <see cref="CosmicWin.Interop.Tests.Win32.Win32NativeWindowSourceRealWindowTests.SetWindowPosition_MovesTheRealWindow_AndSubsequentReadReflectsExactNewBounds"/> on this exact machine/toolchain.</summary>
    private static void AssertTilesExactly(Rect workArea, SplitAxis axis, Rectangle[] rects)
    {
        var ordered = axis == SplitAxis.Horizontal
            ? rects.OrderBy(r => r.Left).ToArray()
            : rects.OrderBy(r => r.Top).ToArray();

        var cursor = axis == SplitAxis.Horizontal ? workArea.X : workArea.Y;
        foreach (var r in ordered)
        {
            if (axis == SplitAxis.Horizontal)
            {
                Assert.Equal(workArea.Y, r.Top);
                Assert.Equal(workArea.Height, r.Height);
                Assert.Equal(cursor, r.Left);
                cursor = r.Right;
            }
            else
            {
                Assert.Equal(workArea.X, r.Left);
                Assert.Equal(workArea.Width, r.Width);
                Assert.Equal(cursor, r.Top);
                cursor = r.Bottom;
            }
        }

        Assert.Equal(axis == SplitAxis.Horizontal ? workArea.X + workArea.Width : workArea.Y + workArea.Height, cursor);
    }

    /// <summary>Constraints 4/5: walks the real Win32 process tree (WMI <c>Win32_Process</c>) to collect the current process and every ancestor, THROWING rather than guessing if it cannot -- called before any window is spawned.</summary>
    private static HashSet<int> ResolveProtectedProcessTree()
    {
        try
        {
            var parentOf = new Dictionary<int, int>();
            using (var searcher = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId FROM Win32_Process"))
            using (var results = searcher.Get())
            {
                foreach (ManagementBaseObject mo in results)
                {
                    parentOf[Convert.ToInt32(mo["ProcessId"])] = Convert.ToInt32(mo["ParentProcessId"]);
                }
            }

            var protectedPids = new HashSet<int> { Environment.ProcessId };
            var current = Environment.ProcessId;
            for (var hop = 0; hop < 64 && parentOf.TryGetValue(current, out var parent); hop++)
            {
                if (parent == 0 || !protectedPids.Add(parent))
                {
                    break;
                }

                current = parent;
            }

            if (protectedPids.Count < 2)
            {
                throw new InvalidOperationException("Resolved zero ancestors for the current process.");
            }

            return protectedPids;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                "FailFast (constraint 5): could not resolve this session's own process ancestry -- refusing to spawn anything blind.", ex);
        }
    }

    /// <summary>Constraint 3: NEVER enumerates or hooks the real desktop -- Open()/Poll() are structurally unreachable (throw). The only way a window reaches the production adapter under test is via RaiseAdded/RaiseRemoved, called only with windows this test itself spawned.</summary>
    private sealed class OwnWindowsOnlyWorkspace : IWorkspace
    {
        public event EventHandler<WindowEventArgs>? WindowAdded;
        public event EventHandler<WindowEventArgs>? WindowRemoved;
        public event EventHandler<WindowEventArgs>? WindowBoundsChanged;

        public bool IsOpen => false;

        public IReadOnlyList<IWindow> Snapshot => Array.Empty<IWindow>();

        public void Open() => throw new NotSupportedException("Never enumerates the real desktop (constraint 3).");

        public void Poll() => throw new NotSupportedException("Never enumerates the real desktop (constraint 3).");

        public void Dispose()
        {
        }

        public void RaiseAdded(IWindow window) => WindowAdded?.Invoke(this, new WindowEventArgs(window));

        public void RaiseRemoved(IWindow window) => WindowRemoved?.Invoke(this, new WindowEventArgs(window));

        /// <summary>
        /// Mirrors what the production <see cref="Win32Workspace"/> does when its
        /// <c>EVENT_OBJECT_LOCATIONCHANGE</c> hook reports that a tracked window's rectangle no
        /// longer matches the one it cached: <c>UpdateBounds</c> raises this event, which is the
        /// ONLY trigger for an earlier decision's snap-back. Without it this shim silently withheld the
        /// single production path that recovers a window which re-sizes itself after being tiled.
        /// </summary>
        public void RaiseBoundsChanged(IWindow window) => WindowBoundsChanged?.Invoke(this, new WindowEventArgs(window));
    }

    /// <summary>
    /// Delegates every member to <paramref name="inner"/> unmodified
    /// -- the real Win32 call still fires, best-effort -- except <see cref="TryActivateWindow"/>,
    /// which additionally RECORDS the targeted handle before delegating. This is what lets step 4
    /// assert WHICH window CosmicWin's own focus path attempts to activate without depending on
    /// Windows' foreground-lock policy, which governs only whether that attempt is actually granted.
    /// </summary>
    private sealed class ActivationRecordingNativeWindowSource(INativeWindowSource inner) : INativeWindowSource
    {
        public nint? LastActivateAttempt { get; private set; }

        public IReadOnlyList<nint> EnumerateTopLevelWindows() => inner.EnumerateTopLevelWindows();

        public bool TryGetWindowInfo(nint hwnd, out NativeWindowInfo info) => inner.TryGetWindowInfo(hwnd, out info);

        public bool SetWindowPosition(nint hwnd, Rectangle bounds) => inner.SetWindowPosition(hwnd, bounds);

        public IDisposable SubscribeWindowEvents(NativeWindowEventCallback callback) => inner.SubscribeWindowEvents(callback);

        public IDisposable SubscribeShownWindows(Action<nint> callback) => inner.SubscribeShownWindows(callback);

        public bool TryActivateWindow(nint hwnd)
        {
            LastActivateAttempt = hwnd;
            return inner.TryActivateWindow(hwnd);
        }
    }

}

