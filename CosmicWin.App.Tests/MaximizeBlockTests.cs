using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App.Tests;

/// <summary>
/// While tiling is active a tiled window cannot be maximized: its maximize button is taken away,
/// and a window that maximizes anyway is put back in its tile.
/// </summary>
/// <remarks>
/// <para>
/// Two layers, because neither is enough alone. Clearing <c>WS_MAXIMIZEBOX</c> disables the
/// caption button, the title-bar double-click and <c>Win+Up</c> for every ordinary framed window,
/// but an application with a custom title bar (Chrome, Electron, Windows Terminal) draws its own
/// button and ignores the style bit. For those the adapter undoes the maximize after the fact and
/// re-arranges the window into its slot.
/// </para>
/// <para>
/// The box is only ever REMOVED from a window that had it, and handed back whenever the window
/// stops being tiled: removed, minimized, evicted, fullscreen, tiling switched off, or the adapter
/// disposed. Never added to a window that never had one.
/// </para>
/// </remarks>
public sealed class MaximizeBlockTests
{
    private static readonly Rectangle Monitor = Rectangle.FromSize(0, 0, 1920, 1080);

    private static readonly Rectangle WorkArea = Rectangle.FromSize(0, 0, 1920, 1032);

    /// <summary>An ordinary framed window: caption, resize border, system menu and both boxes.</summary>
    private const uint BoxedStyle =
        0x00080000u | 0x00010000u | 0x00020000u | WindowStyleFlags.Caption | WindowStyleFlags.ThickFrame;

    private const uint BoxlessStyle = BoxedStyle & ~WindowStyleFlags.MaximizeBox;

    /// <summary>
    /// Admitted ONLY because it carries both boxes: no system menu and no resize border. The
    /// automatic exclusion rules would refuse it the moment the maximize box went.
    /// </summary>
    private const uint AdmittedOnlyByItsBoxes =
        WindowStyleFlags.MaximizeBox | WindowStyleFlags.MinimizeBox | WindowStyleFlags.Caption;

    private const int EnoughRounds = 30;

    /// <summary>Mirrors the adapter's give-up threshold: the restore is asked for at least this often.</summary>
    private const int MaximizeRoundsBeforeEviction = 12;

    private sealed class RecordingTrace : CosmicWin.App.Diagnostics.IDesktopTrace
    {
        public List<string> Lines { get; } = [];

        public void Record(string line) => Lines.Add(line);
    }

    private sealed class Harness : IDisposable
    {
        public Harness(
            uint firstStyle = BoxedStyle,
            uint secondStyle = BoxedStyle,
            bool tilingOn = true,
            Action<RecordingWindow>? configure = null)
        {
            TilingOn = tilingOn;
            var primary = new FakeDisplay(new IntPtr(1), Monitor, WorkArea, 1.0, true);
            Primary = primary;
            Registry = new WindowRegistry();
            Trees = new TreeManager([primary], primary, Registry);
            Workspace = new FakeWorkspace();
            Adapter = new MultiMonitorWorkspaceAdapter(
                Workspace, Trees, Registry, () => ExceptionList.Empty, () => !TilingOn, () => null)
            {
                Trace = Trace,
            };

            First = new RecordingWindow(new IntPtr(10), Rectangle.FromSize(0, 0, 400, 300), style: firstStyle);
            Second = new RecordingWindow(new IntPtr(20), Rectangle.FromSize(0, 0, 400, 300), style: secondStyle);
            configure?.Invoke(First);
            Workspace.RaiseWindowAdded(First);
            Workspace.RaiseWindowAdded(Second);

            FirstTile = First.Bounds;
        }

        /// <summary>The same switch the composition's gate reads: false means tiling is off or paused.</summary>
        public bool TilingOn { get; set; }

        public IDisplay Primary { get; }

        public WindowRegistry Registry { get; }

        public TreeManager Trees { get; }

        public FakeWorkspace Workspace { get; }

        public MultiMonitorWorkspaceAdapter Adapter { get; }

        public RecordingTrace Trace { get; } = new();

        public RecordingWindow First { get; }

        public RecordingWindow Second { get; }

        public Rectangle FirstTile { get; }

        public bool InTree(RecordingWindow window) =>
            Trees.LeavesOn(Primary).Any(leaf => leaf.Window.Handle == window.Handle) &&
            Registry.TryGetLeaf(window.Handle, out _);

        public void Dispose() => Adapter.Dispose();
    }

    private static bool HasMaximizeBox(IWindow window) => (window.Style & WindowStyleFlags.MaximizeBox) != 0;

    // ---- stripping -----------------------------------------------------------------------------

    [Fact]
    public void ATiledWindow_LosesItsMaximizeBox_WhileTilingIsActive()
    {
        using var h = new Harness();

        Assert.False(HasMaximizeBox(h.First));
        Assert.False(HasMaximizeBox(h.Second));
        Assert.True(h.InTree(h.First));
        Assert.Equal(WindowStyleFlags.MinimizeBox, h.First.Style & WindowStyleFlags.MinimizeBox);
    }

    [Fact]
    public void AWindowThatNeverHadAMaximizeBox_IsNeverAskedToChangeOne()
    {
        using var h = new Harness(firstStyle: BoxlessStyle);

        Assert.Empty(h.First.MaximizeBoxRequests);

        h.Adapter.ReleaseMaximizeBlock();

        Assert.Empty(h.First.MaximizeBoxRequests);
        Assert.False(HasMaximizeBox(h.First));
    }

    [Fact]
    public void AWindowOpenedWhileTilingIsOff_IsNotTiledAndKeepsItsMaximizeBox()
    {
        using var h = new Harness(tilingOn: false);

        Assert.True(HasMaximizeBox(h.First));
        Assert.Empty(h.First.MaximizeBoxRequests);
        Assert.False(h.InTree(h.First));
    }

    [Fact]
    public void AWindowThatRefusesTheStyleChange_IsStillTiled_AndTheRefusalIsTraced()
    {
        // An elevated window refuses a style write from a non-elevated CosmicWin. It must stay in
        // the layout, and the refusal must be readable in the trace instead of silent.
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
        var elevated = new RecordingWindow(new IntPtr(30), Rectangle.FromSize(0, 0, 400, 300), style: BoxedStyle)
        {
            RefuseStyleChanges = true,
        };

        workspace.RaiseWindowAdded(elevated);

        Assert.True(registry.TryGetLeaf(elevated.Handle, out _));
        Assert.True(HasMaximizeBox(elevated));
        Assert.Contains(trace.Lines, line => line.StartsWith("maximize box kept hwnd=0x1E ", StringComparison.Ordinal));

        adapter.ReleaseMaximizeBlock();

        // The refused strip, then the quiet give-back attempt a refused strip is still owed (it
        // cannot be told from a timed-out strip that lands later); both refused, nothing changes.
        Assert.Equal([false, true], elevated.MaximizeBoxRequests);
        Assert.DoesNotContain(trace.Lines, line => line.StartsWith("maximize box not returned", StringComparison.Ordinal));
    }

    [Fact]
    public void AStripThatTimedOutButLandedLater_IsStillGivenBackWhenTheBlockIsReleased()
    {
        // The bounded style call answers false when the target does not reply in time, yet the
        // abandoned write may land afterwards. Recording that handle as refused-forever would leave
        // a disabled button nobody knows to give back.
        var primary = new FakeDisplay(new IntPtr(1), Monitor, WorkArea, 1.0, true);
        var registry = new WindowRegistry();
        var trees = new TreeManager([primary], primary, registry);
        var workspace = new FakeWorkspace();
        using var adapter = new MultiMonitorWorkspaceAdapter(
            workspace, trees, registry, () => ExceptionList.Empty, () => false, () => null)
        {
            Trace = new RecordingTrace(),
        };
        var slow = new RecordingWindow(new IntPtr(31), Rectangle.FromSize(0, 0, 400, 300), style: BoxedStyle)
        {
            StripLandsAfterTimeout = true,
        };

        workspace.RaiseWindowAdded(slow);
        Assert.False(HasMaximizeBox(slow)); // the late write landed

        adapter.ReleaseMaximizeBlock();

        Assert.True(HasMaximizeBox(slow));
    }

    [Fact]
    public void AStripThatTimedOutAndLanded_KeepsTheAdmissionVerdict_WhenItIsReEvaluated()
    {
        // R3-admission-verdict-ignores-late-strip: a timed-out strip is UNKNOWN, not refused. This
        // window is tileable only because it carries both boxes; once the abandoned write lands,
        // judging it by its stripped style would evict it, give the box back, re-admit it and flap.
        using var h = new Harness(firstStyle: AdmittedOnlyByItsBoxes, configure: window => window.StripLandsAfterTimeout = true);
        Assert.False(HasMaximizeBox(h.First)); // the late write landed
        Assert.True(h.InTree(h.First));

        for (var round = 0; round < 5; round++)
        {
            h.Workspace.RaiseWindowBoundsChanged(h.First);
        }

        Assert.True(h.InTree(h.First));
        Assert.Equal([false], h.First.MaximizeBoxRequests); // one strip, never flapped
    }

    [Fact]
    public void AStripThatTimedOutAndIsStillPending_IsNotRetriedOnEveryBoundsChange()
    {
        using var h = new Harness(configure: window => window.StripStaysPendingAfterTimeout = true);

        for (var round = 0; round < 5; round++)
        {
            h.Workspace.RaiseWindowBoundsChanged(h.First);
        }

        Assert.Equal([false], h.First.MaximizeBoxRequests);
        Assert.True(h.InTree(h.First));
    }

    [Fact]
    public void AStripStillPendingWhenTheBlockIsReleased_IsGivenBackAfterItLands()
    {
        // R3-late-strip-after-giveback: the give-back reads the box as still present while the
        // abandoned strip has not landed. It must still be ISSUED, because the style call it makes
        // is queued behind the pending strip and so runs after it (pinned in StyleCallQueueTests).
        using var h = new Harness(configure: window => window.StripStaysPendingAfterTimeout = true);
        Assert.True(HasMaximizeBox(h.First)); // the strip has not landed yet

        h.TilingOn = false;
        h.Adapter.ReleaseMaximizeBlock();

        Assert.Equal([false, true], h.First.MaximizeBoxRequests);
        Assert.True(HasMaximizeBox(h.First));
    }

    [Fact]
    public void AWindowThatLeavesTheTreeWhileItIsBeingAdded_IsNeverStripped()
    {
        // The arrange at the end of admission runs a listener, and a listener can end up removing
        // the very window being admitted. Stripping it afterwards would take the button off a
        // window this adapter no longer manages -- and record it as ours to give back later, so
        // the handle would be remembered for good.
        var primary = new FakeDisplay(new IntPtr(1), Monitor, WorkArea, 1.0, true);
        var registry = new WindowRegistry();
        var trees = new TreeManager([primary], primary, registry);
        var workspace = new FakeWorkspace();
        var window = new RecordingWindow(new IntPtr(40), Rectangle.FromSize(0, 0, 400, 300), style: BoxedStyle);
        var removed = false;
        using var adapter = new MultiMonitorWorkspaceAdapter(
            workspace, trees, registry, () => ExceptionList.Empty, () => false, () => null,
            afterArrange: _ =>
            {
                if (!removed)
                {
                    removed = true;
                    workspace.RaiseWindowRemoved(window);
                }
            });

        workspace.RaiseWindowAdded(window);

        Assert.True(removed);
        Assert.False(registry.TryGetLeaf(window.Handle, out _));
        Assert.DoesNotContain(false, window.MaximizeBoxRequests);
        Assert.True(HasMaximizeBox(window));
    }

    // ---- giving it back ------------------------------------------------------------------------

    [Fact]
    public void ARemovedWindow_GetsItsMaximizeBoxBack_WhenItIsStillAlive()
    {
        // Hidden into the notification area: removed from the workspace, still a live window.
        using var h = new Harness();

        h.Workspace.RaiseWindowRemoved(h.First);

        Assert.True(HasMaximizeBox(h.First));
    }

    [Fact]
    public void ARemovedHandle_ForgetsEverything_SoAReusedHandleIsNotTouched()
    {
        using var h = new Harness();
        h.First.Kill();
        h.Workspace.RaiseWindowRemoved(h.First);

        // Windows reuses HWND values: the next window to get 0x0A never had a box to give back.
        var reused = new RecordingWindow(new IntPtr(10), Rectangle.FromSize(0, 0, 400, 300), style: BoxlessStyle);
        h.Workspace.RaiseWindowAdded(reused);
        h.Adapter.ReleaseMaximizeBlock();

        Assert.Empty(reused.MaximizeBoxRequests);
        Assert.False(HasMaximizeBox(reused));
    }

    [Fact]
    public void AMinimizedWindow_GetsItsMaximizeBoxBack_AndLosesItAgainWhenRestored()
    {
        using var h = new Harness();

        h.First.SimulateMinimize();
        h.Workspace.RaiseWindowBoundsChanged(h.First);

        Assert.False(h.InTree(h.First));
        Assert.True(HasMaximizeBox(h.First));

        h.First.SimulateRestore(Rectangle.FromSize(100, 100, 400, 300));
        h.Workspace.RaiseWindowBoundsChanged(h.First);

        Assert.True(h.InTree(h.First));
        Assert.False(HasMaximizeBox(h.First));
    }

    [Fact]
    public void AWindowEvictedAsAFighter_GetsItsMaximizeBoxBack()
    {
        using var h = new Harness();
        h.First.SnapsBackTo = Rectangle.FromSize(5000, 5000, 400, 300);

        for (var round = 0; round < EnoughRounds; round++)
        {
            h.Workspace.RaiseWindowBoundsChanged(h.First);
        }

        Assert.Contains(h.Trace.Lines, line => line.Contains("gave up", StringComparison.Ordinal));
        Assert.False(h.InTree(h.First));
        Assert.True(HasMaximizeBox(h.First));
    }

    [Fact]
    public void AWindowThatGoesFullscreen_GetsItsMaximizeBoxBack_AndLosesItAgainWhenItLeaves()
    {
        using var h = new Harness();

        h.First.SimulateFullscreen(Monitor);
        h.Workspace.RaiseWindowBoundsChanged(h.First);

        Assert.True(HasMaximizeBox(h.First));

        h.First.SimulateLeaveFullscreen(Rectangle.FromSize(100, 100, 400, 300));
        h.Workspace.RaiseWindowBoundsChanged(h.First);

        Assert.True(h.InTree(h.First));
        Assert.False(HasMaximizeBox(h.First));
    }

    [Fact]
    public void ReleasingTheBlock_GivesBackExactlyTheBoxesItTook()
    {
        using var h = new Harness(secondStyle: BoxlessStyle);

        h.Adapter.ReleaseMaximizeBlock();

        Assert.True(HasMaximizeBox(h.First));
        Assert.False(HasMaximizeBox(h.Second));
        Assert.Equal([false, true], h.First.MaximizeBoxRequests);
        Assert.Empty(h.Second.MaximizeBoxRequests);
    }

    [Fact]
    public void AFailedGiveBack_IsTraced_WithTheWindowIdentity()
    {
        // The window refuses the write that would return its button (it turned elevated, or its
        // thread is gone): it is left without one, and that must be readable in the trace.
        using var h = new Harness();
        h.First.RefuseStyleChanges = true;

        h.Adapter.ReleaseMaximizeBlock();

        Assert.Contains(
            h.Trace.Lines,
            line => line.StartsWith("maximize box not returned hwnd=0xA ", StringComparison.Ordinal)
                && line.Contains("class=") && line.Contains("proc="));
    }

    [Fact]
    public void ASuccessfulGiveBack_IsNotTraced()
    {
        using var h = new Harness();

        h.Adapter.ReleaseMaximizeBlock();

        Assert.True(HasMaximizeBox(h.First));
        Assert.DoesNotContain(h.Trace.Lines, line => line.StartsWith("maximize box not returned", StringComparison.Ordinal));
    }

    [Fact]
    public void ReleasingTheBlockTwice_AsksEachWindowOnlyOnce()
    {
        using var h = new Harness();

        h.Adapter.ReleaseMaximizeBlock();
        h.Adapter.ReleaseMaximizeBlock();

        Assert.Equal([false, true], h.First.MaximizeBoxRequests);
    }

    [Fact]
    public void ApplyingTheBlockAgain_TakesTheBoxesOfEveryTiledWindowBack()
    {
        using var h = new Harness();
        h.TilingOn = false;
        h.Adapter.ReleaseMaximizeBlock();
        Assert.True(HasMaximizeBox(h.First));

        h.TilingOn = true;
        h.Adapter.ApplyMaximizeBlock();

        Assert.False(HasMaximizeBox(h.First));
        Assert.False(HasMaximizeBox(h.Second));
    }

    [Fact]
    public void ApplyingTheBlock_PutsBackAWindowThatMaximizedWhileTilingWasOff()
    {
        using var h = new Harness();
        h.TilingOn = false;
        h.Adapter.ReleaseMaximizeBlock();
        h.First.SimulateMaximize(WorkArea);

        h.TilingOn = true;
        h.Adapter.ApplyMaximizeBlock();

        Assert.Equal(1, h.First.RestoreCallCount);
        Assert.Equal(0u, h.First.Style & WindowStyleFlags.Maximized);
        Assert.False(HasMaximizeBox(h.First));
    }

    [Fact]
    public void ApplyingTheBlock_AfterRestoringSeveralWindows_GivesTheForegroundBackToWhoHadIt()
    {
        // SW_RESTORE activates what it restores, so restoring two maximized windows in a row leaves
        // the foreground on whichever came last. The user was working in the FIRST one, which is restored first and so ends up behind.
        using var h = new Harness();
        var log = new List<nint>();
        h.First.ActivationLog = log;
        h.Second.ActivationLog = log;
        h.First.RestoreActivates = true;
        h.Second.RestoreActivates = true;
        h.TilingOn = false;
        h.Adapter.ReleaseMaximizeBlock();
        h.First.SimulateMaximize(WorkArea);
        h.Second.SimulateMaximize(WorkArea);
        log.Clear();

        h.TilingOn = true;
        h.Adapter.ApplyMaximizeBlock(foregroundBefore: h.First.Handle);

        Assert.Equal(1, h.First.RestoreCallCount);
        Assert.Equal(1, h.Second.RestoreCallCount);
        Assert.Equal(h.First.Handle, log[^1]);
    }

    [Fact]
    public void ApplyingTheBlock_WhenNothingWasRestored_NeverTouchesTheForeground()
    {
        using var h = new Harness();
        var log = new List<nint>();
        h.First.ActivationLog = log;
        h.Second.ActivationLog = log;
        h.TilingOn = false;
        h.Adapter.ReleaseMaximizeBlock();

        h.TilingOn = true;
        h.Adapter.ApplyMaximizeBlock(foregroundBefore: h.Second.Handle);

        Assert.Empty(log);
    }

    [Fact]
    public void ApplyingTheBlock_WhenTheForegroundIsNotATiledWindow_ActivatesNothingExtra()
    {
        using var h = new Harness();
        var log = new List<nint>();
        h.First.ActivationLog = log;
        h.First.RestoreActivates = true;
        h.TilingOn = false;
        h.Adapter.ReleaseMaximizeBlock();
        h.First.SimulateMaximize(WorkArea);
        log.Clear();

        h.TilingOn = true;
        h.Adapter.ApplyMaximizeBlock(foregroundBefore: new IntPtr(999));

        Assert.Equal([h.First.Handle], log); // only the restore's own activation
    }

    [Fact]
    public void ApplyingTheBlock_WhilePaused_ChangesNothing()
    {
        using var h = new Harness();
        h.Adapter.ReleaseMaximizeBlock();
        var requests = h.First.MaximizeBoxRequests.Count;

        h.TilingOn = false;
        h.Adapter.ApplyMaximizeBlock();

        Assert.Equal(requests, h.First.MaximizeBoxRequests.Count);
        Assert.True(HasMaximizeBox(h.First));
    }

    [Fact]
    public void ApplyingTheBlock_LeavesAFullscreenWindowAlone()
    {
        using var h = new Harness();
        h.First.SimulateFullscreen(Monitor);
        h.Workspace.RaiseWindowBoundsChanged(h.First);
        h.Adapter.ReleaseMaximizeBlock();
        var requests = h.First.MaximizeBoxRequests.Count;

        h.Adapter.ApplyMaximizeBlock();

        Assert.Equal(requests, h.First.MaximizeBoxRequests.Count);
        Assert.True(HasMaximizeBox(h.First));
    }

    [Fact]
    public void DisposingTheAdapter_GivesEveryBoxBack()
    {
        var h = new Harness();

        h.Dispose();

        Assert.True(HasMaximizeBox(h.First));
        Assert.True(HasMaximizeBox(h.Second));
    }

    // ---- admission verdict ---------------------------------------------------------------------

    /// <summary>
    /// The trap in clearing the box: the admission rules read it. This window is tileable only
    /// because it carries BOTH boxes, so a re-evaluation against its stripped style would exclude it
    /// -- the adapter would evict it, hand the box back, re-admit it, strip it again, and flap.
    /// </summary>
    [Fact]
    public void AStrippedWindow_KeepsItsAdmissionVerdict_WhenItIsReEvaluated()
    {
        using var h = new Harness(firstStyle: AdmittedOnlyByItsBoxes);
        Assert.True(h.InTree(h.First));
        Assert.False(HasMaximizeBox(h.First));

        for (var round = 0; round < 5; round++)
        {
            h.Workspace.RaiseWindowBoundsChanged(h.First);
        }

        Assert.True(h.InTree(h.First));
        Assert.False(HasMaximizeBox(h.First));
        Assert.Equal([false], h.First.MaximizeBoxRequests); // one strip, never flapped
    }

    // ---- fallback ------------------------------------------------------------------------------

    [Fact]
    public void AWindowThatMaximizesAnyway_IsRestoredAndPutBackInItsTile()
    {
        using var h = new Harness();
        h.First.IgnoresMaximizeBox = true;

        h.First.SimulateMaximize(WorkArea);
        h.Workspace.RaiseWindowBoundsChanged(h.First);

        Assert.Equal(1, h.First.RestoreCallCount);
        Assert.Equal(0u, h.First.Style & WindowStyleFlags.Maximized);
        Assert.Equal(h.FirstTile, h.First.Bounds);
        Assert.True(h.InTree(h.First));
    }

    /// <summary>
    /// R3-restore-lane-lost-update-race: the queue cannot stop a strip (ordered lane) from writing back
    /// a stale WS_MAXIMIZE after a restore (independent lane) cleared it on a hung window -- Win32 has no
    /// single-bit style write. The documented answer is that the NEXT bounds change re-checks the bit.
    /// This pins that self-healing: a window left in its tile with only the stale bit set is restored
    /// on its next bounds change and keeps its tile.
    /// </summary>
    [Fact]
    public void AStaleMaximizedBitLeftByAStripRacingARestore_IsClearedOnTheNextBoundsChange()
    {
        using var h = new Harness();
        Assert.Equal(h.FirstTile, h.First.Bounds);

        h.First.SimulateStaleMaximizedBit();
        h.Workspace.RaiseWindowBoundsChanged(h.First);

        Assert.Equal(1, h.First.RestoreCallCount);
        Assert.Equal(0u, h.First.Style & WindowStyleFlags.Maximized);
        Assert.Equal(h.FirstTile, h.First.Bounds);
        Assert.True(h.InTree(h.First));
    }

    [Fact]
    public void AWindowThatMaximizesAnyway_IsRestoredThroughAUserGestureToo()
    {
        // Aero Snap to the top edge ends in MOVESIZEEND and arrives flagged as the user's own.
        using var h = new Harness();

        h.First.SimulateMaximize(WorkArea);
        h.Workspace.RaiseWindowBoundsChanged(h.First, isUserGesture: true);

        Assert.Equal(1, h.First.RestoreCallCount);
        Assert.Equal(h.FirstTile, h.First.Bounds);
    }

    /// <summary>
    /// A window that keeps maximizing is a user (or an app) at work, not a fighter: restoring it is
    /// the correct answer every time, and evicting it for the repetition would leave it maximized.
    /// </summary>
    [Fact]
    public void RepeatedMaximizing_NeverCountsTowardEviction()
    {
        using var h = new Harness();

        for (var round = 0; round < EnoughRounds; round++)
        {
            h.First.SimulateMaximize(WorkArea);
            h.Workspace.RaiseWindowBoundsChanged(h.First);
        }

        Assert.True(h.InTree(h.First));
        Assert.DoesNotContain(h.Trace.Lines, line => line.Contains("gave up", StringComparison.Ordinal));
        Assert.Equal(EnoughRounds, h.First.RestoreCallCount);
        Assert.Equal(h.FirstTile, h.First.Bounds);
    }

    [Fact]
    public void AFullscreenWindow_IsNeverRestored()
    {
        using var h = new Harness();

        h.First.SimulateFullscreen(Monitor);
        for (var round = 0; round < EnoughRounds; round++)
        {
            h.Workspace.RaiseWindowBoundsChanged(h.First);
        }

        Assert.Equal(0, h.First.RestoreCallCount);
        Assert.Equal(Monitor, h.First.Bounds);
        Assert.True(h.InTree(h.First));
    }

    [Fact]
    public void WhileTilingIsOff_AWindowMayMaximize_AndNothingIsRestored()
    {
        using var h = new Harness();
        h.TilingOn = false;
        h.Adapter.ReleaseMaximizeBlock();

        h.First.SimulateMaximize(WorkArea);
        h.Workspace.RaiseWindowBoundsChanged(h.First);

        Assert.Equal(0, h.First.RestoreCallCount);
        Assert.NotEqual(0u, h.First.Style & WindowStyleFlags.Maximized);
        Assert.Equal(WorkArea, h.First.Bounds);
    }

    [Fact]
    public void AMaximizedWindowThatCannotBeRestored_StillFallsUnderTheFighterGuard()
    {
        // The guard is a safety net, and a window the OS will not restore is exactly what it is for.
        //
        // The window maximizes the way a real one does: its frame overshoots the work area by the
        // invisible border, so it does NOT land on the corner of its tile. That matters. A window
        // that obeys its corner and fills the work area exactly is read as a MINIMUM-SIZE window and
        // untiled by that path instead -- an earlier version of this fact used such a window and
        // passed without ever reaching the guard (no "gave up" line), so it would have passed against
        // an adapter that never attempted the restore at all.
        using var h = new Harness();
        h.First.RefuseStyleChanges = true;
        var overshoot = Rectangle.FromSize(
            WorkArea.Left - 8, WorkArea.Top - 8, WorkArea.Width + 16, WorkArea.Height + 16);

        for (var round = 0; round < EnoughRounds; round++)
        {
            h.First.SimulateMaximize(overshoot);
            h.Workspace.RaiseWindowBoundsChanged(h.First);
        }

        // Evicted and refused for good, by the guard, and the trace names the reason.
        Assert.False(h.InTree(h.First));
        Assert.Contains(h.Trace.Lines, line => line.StartsWith("gave up hwnd=0xA ", StringComparison.Ordinal));
        Assert.DoesNotContain(h.Trace.Lines, line => line.StartsWith("minimum size", StringComparison.Ordinal));

        // The restore was genuinely ATTEMPTED every round until the guard fired, and every refusal
        // was reported as such, never as a success.
        Assert.True(h.First.RestoreCallCount >= MaximizeRoundsBeforeEviction);
        Assert.DoesNotContain(h.Trace.Lines, line => line.StartsWith("maximize undone", StringComparison.Ordinal));
        Assert.Contains(h.Trace.Lines, line => line.StartsWith("maximize kept hwnd=0xA ", StringComparison.Ordinal));

        // Nothing pretends it was put back: it is still maximized.
        Assert.NotEqual(0u, h.First.Style & WindowStyleFlags.Maximized);
    }

    [Fact]
    public void AMaximizedWindowThatCannotBeRestored_IsNotEvictedBeforeTheGuardThreshold()
    {
        // The other side of the guard: a single refused restore is a wobble, not a fight.
        using var h = new Harness();
        h.First.RefuseStyleChanges = true;

        h.First.SimulateMaximize(Rectangle.FromSize(-8, -8, WorkArea.Width + 16, WorkArea.Height + 16));
        h.Workspace.RaiseWindowBoundsChanged(h.First);

        Assert.True(h.InTree(h.First));
        Assert.Equal(1, h.First.RestoreCallCount);
    }
}
