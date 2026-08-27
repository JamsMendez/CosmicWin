using CosmicWin.App.Input;
using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;
using CosmicWin.Layout;

namespace CosmicWin.App.Tests;

/// <summary>
/// Where focus goes when the windows on screen change desktop: the focused one sent away, and the
/// user walking to another desktop themselves.
/// </summary>
/// <remarks>
/// <para>
/// Reported from real use: <c>Alt+Shift+N</c> sent a window away and CosmicWin went on treating it
/// as the focused one, so the next focus chord walked from a tile that was no longer on screen.
/// </para>
/// <para>
/// The cause is that <c>_focused</c>'s only liveness test is <c>IsAlive</c>, and a window parked on
/// another desktop is perfectly alive -- DWM CLOAKS it, it does not destroy it, and this repository
/// has already measured that cloaking leaves <c>WS_VISIBLE</c> set. So the cache cannot tell "the
/// user switched away from this window" from "this window still exists somewhere", and it kept
/// answering with the one thing that was certainly wrong.
/// </para>
/// </remarks>
public sealed class ActionExecutorDesktopFocusTests
{
    private sealed class FakeVirtualDesktops : IVirtualDesktopService
    {
        /// <summary>Set to false to reproduce an unrecognised Windows build.</summary>
        public bool Supported { get; set; } = true;

        public bool IsSupported => Supported;
        public int Count => 2;
        public int CurrentIndex => 1;
        public Guid CurrentDesktopId { get; set; } = Guid.Empty;
        public string? LastError => null;

        /// <summary>Set to false to reproduce a shell that REFUSED the move.</summary>
        public bool MoveSucceeds { get; set; } = true;

        /// <summary>Set to false to reproduce a shell that REFUSED the switch.</summary>
        public bool SwitchSucceeds { get; set; } = true;

        /// <summary>Where a successful switch lands, so a test can drive the arriving desktop.</summary>
        public Guid SwitchesTo { get; set; } = Guid.Empty;

        public List<(nint Handle, int Index)> Moved { get; } = [];

        /// <summary>
        /// Runs INSIDE TryMoveWindowTo, before it reports anything. It is how a fact can look at the
        /// world at the exact instant the window leaves, which is the only way to assert that focus
        /// had already gone somewhere else by then.
        /// </summary>
        public Action? AtTheMoment { get; set; }

        public bool TrySwitchTo(int oneBasedIndex)
        {
            if (!SwitchSucceeds)
            {
                return false;
            }

            // The real service VERIFIES the switch landed before reporting success, so the id it
            // reports afterwards is the ARRIVING desktop. Modelling that is the whole point here.
            CurrentDesktopId = SwitchesTo;
            return true;
        }

        public bool TryMoveWindowTo(nint windowHandle, int oneBasedIndex)
        {
            AtTheMoment?.Invoke();

            if (!MoveSucceeds)
            {
                return false;
            }

            Moved.Add((windowHandle, oneBasedIndex));
            return true;
        }
    }

    /// <summary>
    /// Local rather than shared: every other executor suite declares its own, and the whole type is
    /// one settable property.
    /// </summary>
    private sealed class FakeForegroundWindowSource : IForegroundWindowSource
    {
        public nint Handle { get; set; }

        /// <summary>
        /// What each window's OWNING UI THREAD believes is active, keyed by that window's handle.
        /// Deliberately separate from <see cref="Handle"/>: the two disagreeing is not a bug in this
        /// double, it is the exact state under investigation -- a thread left holding activation
        /// after an <c>AttachThreadInput</c> detach, still painting itself active while the OS
        /// foreground belongs to someone else.
        /// </summary>
        public Dictionary<nint, nint> ThreadActiveWindow { get; } = [];

        public nint GetForegroundHandle() => Handle;

        public nint GetActiveWindowOfThreadOwning(nint hwnd) =>
            ThreadActiveWindow.TryGetValue(hwnd, out var active) ? active : 0;
    }

    /// <summary>Collects the desktop-trace lines a fact wants to read back.</summary>
    private sealed class RecordingDesktopTrace : CosmicWin.App.Diagnostics.IDesktopTrace
    {
        public List<string> Lines { get; } = [];

        public void Record(string line) => Lines.Add(line);
    }

    private sealed record Harness(
        ActionExecutor Executor,
        FakeVirtualDesktops Desktops,
        FakeForegroundWindowSource Foreground,
        RecordingWindow Focused,
        RecordingWindow Survivor,
        LayoutTree Tree,
        LeafNode FocusedLeaf,
        LeafNode SurvivorLeaf,
        RecordingDesktopTrace Trace,
        List<nint> ActivationOrder);

    /// <summary>
    /// The sweep is bounded as a WHOLE, not per window. Each activation already has its own bound
    /// in the interop; N of them in a row had none, and this walk runs on the WPF UI thread.
    /// </summary>
    /// <remarks>
    /// The budget gates STARTING the next activation rather than interrupting one in flight, so the
    /// windows past the line keep the stale border they already had -- what we came to fix failing
    /// to help, never a new harm. Focus is untouched either way, which this pins by asserting the
    /// landing window is still activated LAST after the walk gave up.
    /// </remarks>
    [Fact]
    public void TheArrivalSweep_IsBoundedForTheWholeWalk_NotPerWindow()
    {
        var (executor, order, trace, handles, _) = BuildWideDesktop(tiles: 4);

        // 200ms per reading: the first swept window fits inside the 250ms budget, the rest do not.
        var readings = 0;
        var origin = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        executor.Clock = () => origin.AddMilliseconds(200 * readings++);

        executor.HandFocusToArrivingDesktop();

        // One swept, then the landing window -- NOT all four.
        Assert.Equal([handles[1], handles[0]], order);
        Assert.Contains("budget-exhausted=2", Assert.Single(trace.Lines, l => l.StartsWith("sweep ", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    /// <summary>
    /// What a REFUSED survivor activation leaves behind once a sweep has run. Pinned deliberately,
    /// because the sweep changed it and nothing said so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before the sweep, a refused handover left the foreground exactly where the shell had put it
    /// -- which, on the arriving path, is the defect this whole area exists to fix: focus left
    /// behind on the desktop just abandoned. After the sweep it lands on the last window the sweep
    /// reached, which is an arbitrary tile of the RIGHT desktop. That is a strictly better failure
    /// than the one it replaced, and a review reading it as a regression had the direction backwards.
    /// </para>
    /// <para>
    /// Never observed: thirty-five traced handovers, thirty-five confirmed. This is a contract for
    /// the case the machine has not produced yet, not a repair.
    /// </para>
    /// <para>
    /// This pins WHERE the walk ended, and nothing else. The other half -- that a refused
    /// activation must not advance the remembered focus -- is already held by
    /// <see cref="WhenActivatingTheSurvivorFails_ItDoesNotBecomeTheRememberedFocus"/>, and this
    /// fact does NOT hold it: <c>activated=False</c> is traced either way, so the assertion below
    /// stays green with that rule broken. Verified by mutation rather than assumed, after the doc
    /// here first claimed the opposite.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheArrivalSweep_SurvivorRefused_EndsOnTheLastSweptWindow_AndClaimsNothing()
    {
        var (executor, order, trace, handles, windows) = BuildWideDesktop(tiles: 3);
        executor.Clock = () => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        windows[0].NextActivation = ActivationOutcome.Failed;

        executor.HandFocusToArrivingDesktop();

        // The survivor was asked LAST and said no, so the last activation that took is the sweep's.
        Assert.Equal([handles[1], handles[2], handles[0]], order);
        Assert.Contains(
            "activation=Failed activated=False",
            Assert.Single(trace.Lines, l => l.StartsWith("handover ", StringComparison.Ordinal)),
            StringComparison.Ordinal);
    }

    /// <summary>The same desktop with a clock that never advances sweeps every tile, so the bound is what stopped it.</summary>
    [Fact]
    public void TheArrivalSweep_WithinBudget_StillVisitsEveryWindow()
    {
        var (executor, order, trace, handles, _) = BuildWideDesktop(tiles: 4);
        executor.Clock = () => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        executor.HandFocusToArrivingDesktop();

        Assert.Equal([handles[1], handles[2], handles[3], handles[0]], order);
        Assert.DoesNotContain("budget-exhausted", Assert.Single(trace.Lines, l => l.StartsWith("sweep ", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    /// <summary>A row of <paramref name="tiles"/> windows; the first is where focus lands.</summary>
    private static (ActionExecutor Executor, List<nint> Order, RecordingDesktopTrace Trace, nint[] Handles, RecordingWindow[] Windows)
        BuildWideDesktop(int tiles)
    {
        var registry = new WindowRegistry();
        var group = new GroupNode(SplitAxis.Horizontal) { GroupLength = 1920 };
        var order = new List<nint>();
        var handles = new nint[tiles];
        var windows = new RecordingWindow[tiles];

        for (var index = 0; index < tiles; index++)
        {
            var handle = new IntPtr(0xE0 + index);
            handles[index] = handle;
            var leaf = new LeafNode(new WindowRef(handle)) { Parent = group };
            group.Children.Add(leaf);
            group.Sizes.Add(1920 / tiles);
            var window = new RecordingWindow(handle, Rectangle.FromSize(0, 0, 1920 / tiles, 1080))
            {
                ActivationLog = order,
            };
            windows[index] = window;
            registry.Register(window, leaf);
        }

        var display = new FakeDisplay(
            new IntPtr(1), Rectangle.FromSize(0, 0, 1920, 1080), Rectangle.FromSize(0, 0, 1920, 1080), 1.0, true);
        var treeManager = new TreeManager([display], display, registry);
        treeManager.TryGetTree(display, out var managed);
        managed!.Root = group;

        var trace = new RecordingDesktopTrace();
        var executor = new ActionExecutor(
            managed, registry, new FakeForegroundWindowSource { Handle = handles[0] })
        {
            WorkArea = new Rect(0, 0, 1920, 1080),
            TreeManager = treeManager,
            VirtualDesktops = new FakeVirtualDesktops(),
            DesktopTrace = trace,
        };

        return (executor, order, trace, handles, windows);
    }

    /// <summary>
    /// Two tiles, with the FOCUSED one deliberately FIRST in the tree. That ordering is the point:
    /// a survivor search that simply took the tree's first leaf would hand focus straight back to
    /// the window it just sent away, and this arrangement is what catches that.
    /// </summary>
    private static Harness Build(bool withSurvivor = true)
    {
        var focusedLeaf = new LeafNode(new WindowRef(new IntPtr(0xF1)));
        var survivorLeaf = new LeafNode(new WindowRef(new IntPtr(0xF2)));

        var registry = new WindowRegistry();
        var focused = new RecordingWindow(focusedLeaf.Window.Handle, Rectangle.FromSize(0, 0, 960, 1080));
        var survivor = new RecordingWindow(survivorLeaf.Window.Handle, Rectangle.FromSize(960, 0, 960, 1080));

        LayoutTree tree;
        if (withSurvivor)
        {
            var group = new GroupNode(SplitAxis.Horizontal) { GroupLength = 1920 };
            foreach (var leaf in new[] { focusedLeaf, survivorLeaf })
            {
                group.Children.Add(leaf);
                group.Sizes.Add(960);
                leaf.Parent = group;
            }

            tree = new LayoutTree(group);
            registry.Register(focused, focusedLeaf);
            registry.Register(survivor, survivorLeaf);
        }
        else
        {
            tree = new LayoutTree(focusedLeaf);
            registry.Register(focused, focusedLeaf);
        }

        var display = new FakeDisplay(
            new IntPtr(1), Rectangle.FromSize(0, 0, 1920, 1080), Rectangle.FromSize(0, 0, 1920, 1080), 1.0, true);
        var treeManager = new TreeManager([display], display, registry);
        treeManager.TryGetTree(display, out var managed);
        managed!.Root = tree.Root;

        var foreground = new FakeForegroundWindowSource { Handle = focused.Handle };
        var desktops = new FakeVirtualDesktops();
        var trace = new RecordingDesktopTrace();

        // Shared by both windows, so the ORDER activations happened in is readable. Counts answer
        // "how many"; anything that walks a set of windows is only correct if it also ends in the
        // right place, and that is a question only an ordered log can answer.
        var activationOrder = new List<nint>();
        focused.ActivationLog = activationOrder;
        survivor.ActivationLog = activationOrder;
        var executor = new ActionExecutor(managed, registry, foreground)
        {
            WorkArea = new Rect(0, 0, 1920, 1080),
            TreeManager = treeManager,
            VirtualDesktops = desktops,
            DesktopTrace = trace,
        };

        // Production wires this to MultiMonitorWorkspaceAdapter.RehomeToDesktop, which takes the
        // window OUT of the tree it left. Without it here the departed leaf would still be sitting
        // in the tree the survivor search reads, and these facts would be measuring a world that
        // does not exist.
        executor.WindowMovedToDesktop = handle =>
        {
            if (registry.TryGetLeaf(handle, out var leaf) && leaf is not null)
            {
                managed.Remove(leaf);
            }
        };

        return new Harness(
            executor, desktops, foreground, focused, survivor, managed, focusedLeaf, survivorLeaf,
            trace, activationOrder);
    }

    /// <summary>Two desktops with stable identities, so a fact can walk between them and back.</summary>
    private static readonly Guid DesktopOne = new("11111111-1111-1111-1111-111111111111");

    private static readonly Guid DesktopTwo = new("22222222-2222-2222-2222-222222222222");

    /// <summary>
    /// Walks the user from <paramref name="from"/> to <paramref name="to"/> with the switch chord,
    /// the way the real one does: the executor reads the foreground FIRST, which is what records
    /// who was left behind.
    /// </summary>
    private static async Task Switch(Harness harness, Guid from, Guid to, nint focusedThere)
    {
        harness.Desktops.CurrentDesktopId = from;
        harness.Desktops.SwitchesTo = to;
        harness.Foreground.Handle = focusedThere;

        await harness.Executor
            .ScheduleAsync(new HotkeyAction(HotkeyActionKind.SwitchDesktop, 1), CancellationToken.None)
            .AsTask();
    }

    private static Task Send(Harness harness, int desktop = 2) =>
        harness.Executor
            .ScheduleAsync(new HotkeyAction(HotkeyActionKind.MoveWindowToDesktop, desktop), CancellationToken.None)
            .AsTask();

    /// <summary>
    /// The reported defect. Sending the focused window away must hand focus to a window still on
    /// this desktop -- and NOT back to the one that just left, which is why it is first in the tree.
    /// </summary>
    [Fact]
    public async Task SendingTheFocusedWindowAway_HandsFocusToAWindowStillOnThisDesktop()
    {
        var harness = Build();

        await Send(harness);

        Assert.Equal(1, harness.Survivor.TryActivateCallCount);
        Assert.Equal(0, harness.Focused.TryActivateCallCount);
    }

    /// <summary>
    /// No window left means NO focus. Inventing one would drag the user to a tile they never asked
    /// for, and there is nothing on this desktop to drag them to anyway.
    /// </summary>
    [Fact]
    public async Task WithNothingLeftOnThisDesktop_NothingIsActivated()
    {
        var harness = Build(withSurvivor: false);

        await Send(harness);

        Assert.Equal(0, harness.Focused.TryActivateCallCount);
    }

    /// <summary>
    /// Reported from real use: after sending the focused window away, BOTH it and the newly focused
    /// window wore an accent border, even with CosmicWin's own border switched off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The border was never CosmicWin's -- nothing in production writes <c>DWMWA_BORDER_COLOR</c>,
    /// and the overlay was off. It was DWM's own, painted on two windows at once, and the cause was
    /// ORDER: the window was moved FIRST and focus was handed on afterwards. By then the departing
    /// window was already on another desktop and cloaked, so it never got a deactivation it could
    /// repaint while anybody could see it, and it kept wearing its active frame.
    /// </para>
    /// <para>
    /// So focus leaves before the window does. This fact reads the survivor's activation count at
    /// the exact instant the shell is asked to move anything, which is the only place the ordering
    /// is observable.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task FocusLeavesTheWindow_BeforeTheWindowLeavesTheDesktop()
    {
        var harness = Build();
        var activationsWhenItLeft = -1;
        harness.Desktops.AtTheMoment = () => activationsWhenItLeft = harness.Survivor.TryActivateCallCount;

        await Send(harness);

        Assert.Equal(1, activationsWhenItLeft);
    }

    /// <summary>
    /// The departing window is never chosen as its own survivor, and now that really is load-bearing
    /// rather than defensive.
    /// </summary>
    /// <remarks>
    /// Focus is handed on BEFORE the move, so the window being sent away is still sitting in the
    /// tree the survivor search reads -- and it is deliberately FIRST in that tree. Without the
    /// exclusion the search would hand focus straight back to the window it is trying to get focus
    /// off, which would reproduce the reported defect exactly.
    /// </remarks>
    [Fact]
    public async Task TheDepartingWindowIsNeverItsOwnSurvivor_EvenThoughItIsStillInTheTree()
    {
        var harness = Build();
        LeafNode? inTreeWhenItLeft = null;
        harness.Desktops.AtTheMoment = () =>
            inTreeWhenItLeft = harness.Tree.Root is GroupNode group
                ? group.Children.OfType<LeafNode>().FirstOrDefault()
                : harness.Tree.Root as LeafNode;

        await Send(harness);

        // The premise this fact rests on: the departing window really was still the first leaf.
        Assert.Equal(harness.FocusedLeaf, inTreeWhenItLeft);
        Assert.Equal(1, harness.Survivor.TryActivateCallCount);
        Assert.Equal(0, harness.Focused.TryActivateCallCount);
    }

    /// <summary>
    /// The handover says what it did, on every outcome.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written because a reported defect could not be diagnosed without it. The handover picks a
    /// survivor and activates it, and from outside NONE of that is visible: whether a survivor was
    /// found, which window it was, or whether the activation was refused. Twice in one session that
    /// gap forced a guess where a fact would have done.
    /// </para>
    /// <para>
    /// This is the repository's own standing rule, already written on FileDesktopTrace after the
    /// desktop chords reported "Alt+N does nothing" with no way to tell why: instrument before
    /// guessing, because a window manager's failures are invisible by nature.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheHandover_RecordsTheSurvivorItChoseAndWhetherActivationWorked()
    {
        var harness = Build();

        await Send(harness);

        var line = Assert.Single(harness.Trace.Lines, l => l.StartsWith("handover ", StringComparison.Ordinal));
        Assert.Contains($"departing=0x{harness.Focused.Handle:X}", line, StringComparison.Ordinal);
        Assert.Contains($"survivor=0x{harness.Survivor.Handle:X}", line, StringComparison.Ordinal);
        Assert.Contains("activated=True", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The line carries the OS foreground from BEFORE and AFTER the handover.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This pins the SHAPE, because a fake window cannot move a real foreground; what the two
    /// readings are worth only shows on hardware. But the shape is what makes the diagnosis
    /// possible at all: <c>activated</c> is <see cref="IWindow.TryActivate"/>'s bool, and a bool
    /// cannot tell a genuine foreground change from <c>AlreadyForeground</c> -- the short-circuit
    /// that reports success while nothing moved on screen, which is exactly how MR-2 hid for four
    /// supervised runs.
    /// </para>
    /// <para>
    /// Read back from the injected foreground source rather than assumed, so a handover that
    /// believed it worked and a handover that actually worked stop looking identical in the log.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheHandover_RecordsTheForegroundBeforeAndAfterItself()
    {
        var harness = Build();

        await Send(harness);

        var line = Assert.Single(harness.Trace.Lines, l => l.StartsWith("handover ", StringComparison.Ordinal));
        Assert.Contains($"fg-before=0x{harness.Focused.Handle:X}", line, StringComparison.Ordinal);
        Assert.Contains("fg-after=0x", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// A REFUSED activation is the outcome the trace exists for: focus silently stayed where it was,
    /// and nothing else on the machine says so.
    /// </summary>
    [Fact]
    public async Task WhenActivationIsRefused_TheHandoverSaysSo()
    {
        var harness = Build();
        harness.Survivor.FailNextActivate();

        await Send(harness);

        var line = Assert.Single(harness.Trace.Lines, l => l.StartsWith("handover ", StringComparison.Ordinal));
        Assert.Contains("activated=False", line, StringComparison.Ordinal);
    }

    /// <summary>An empty desktop is a legitimate answer, and it has to be legible as one.</summary>
    [Fact]
    public async Task WithNoSurvivor_TheHandoverSaysThatToo()
    {
        var harness = Build(withSurvivor: false);

        await Send(harness);

        var line = Assert.Single(harness.Trace.Lines, l => l.StartsWith("handover ", StringComparison.Ordinal));
        Assert.Contains("no survivor", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The handover names WHICH RUNG of the activation escalation ran, not merely that something
    /// worked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Win32NativeWindowSource.Activate</c> distinguishes six endings -- already foreground, a
    /// plain <c>SetForegroundWindow</c>, an <c>AttachThreadInput</c>-backed retry, synthetic Alt
    /// taps, a flat refusal, and a timed-out worker -- and every one of them was being collapsed
    /// into one <see cref="bool"/> before it reached anybody who could read it. Three of those
    /// endings are successes with completely different consequences for the machine, so the bool
    /// answers the one question nobody was asking.
    /// </para>
    /// <para>
    /// The rung matters because <c>AttachThreadInput</c> is the only one that touches another
    /// process's input state, and a departing handover runs while the window being sent away still
    /// holds the foreground. If the desktop chord reports an attached rung while an ordinary
    /// same-desktop focus change reports <see cref="ActivationOutcome.Direct"/>, that difference is
    /// the whole diagnosis; with a bool it is unreadable either way.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheHandover_RecordsWhichActivationRungRan()
    {
        var harness = Build();
        harness.Survivor.NextActivation = ActivationOutcome.InputUnlocked;

        await Send(harness);

        var line = Assert.Single(harness.Trace.Lines, l => l.StartsWith("handover ", StringComparison.Ordinal));
        Assert.Contains("activation=InputUnlocked", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// A refused activation names its ending too, and the two refusals stay distinguishable.
    /// </summary>
    /// <remarks>
    /// <see cref="ActivationOutcome.Failed"/> is Windows saying no; <see
    /// cref="ActivationOutcome.TimedOut"/> is our own budget expiring before the worker was even
    /// scheduled, which answers nothing about what Windows would have said. They call for opposite
    /// fixes, and the bool spelled both of them <c>false</c>.
    /// </remarks>
    [Fact]
    public async Task WhenActivationTimesOut_TheHandoverSaysThat_NotMerelyThatItFailed()
    {
        var harness = Build();
        harness.Survivor.NextActivation = ActivationOutcome.TimedOut;

        await Send(harness);

        var line = Assert.Single(harness.Trace.Lines, l => l.StartsWith("handover ", StringComparison.Ordinal));
        Assert.Contains("activation=TimedOut", line, StringComparison.Ordinal);
        Assert.Contains("activated=False", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The line carries what the DEPARTING window's own UI thread still believes is active.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the second reading, and the one that can turn the standing hypothesis into a
    /// measurement. Windows keeps activation state per input queue as well as globally, and a
    /// thread that was attached to ours and then detached is not reliably told it lost the
    /// foreground. Chromium paints its non-client frame from that thread-local state, which is why
    /// a window sent to another desktop can keep wearing an active border while
    /// <c>GetForegroundWindow</c> names somebody else entirely.
    /// </para>
    /// <para>
    /// Recorded beside <c>fg-after</c> on purpose: neither reading proves anything alone. The two
    /// DISAGREEING is the finding -- the departing thread naming itself while the OS names the
    /// survivor -- and that comparison is only possible if both are on the same line.
    /// </para>
    /// <para>
    /// A fake cannot own a real input queue, so this pins the SHAPE. What the reading is worth
    /// shows only on hardware; what it costs to be missing has already been paid twice.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheHandover_RecordsWhatTheDepartingWindowsOwnThreadStillBelievesIsActive()
    {
        var harness = Build();
        harness.Foreground.ThreadActiveWindow[harness.Focused.Handle] = harness.Focused.Handle;

        await Send(harness);

        // On the SEND path fg-before and the departing handle are the same window, which is why one
        // reading serves both paths -- see the arriving-path fact below for the case where they are
        // not.
        var line = Assert.Single(harness.Trace.Lines, l => l.StartsWith("handover ", StringComparison.Ordinal));
        Assert.Contains(
            $"fg-before-thread-active=0x{harness.Focused.Handle:X}", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The thread reading is keyed to the window that ACTUALLY held focus, which on a plain desktop
    /// switch is not the departing handle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured gap in the first version of this instrument. <c>HandFocusToArrivingDesktop</c> calls
    /// in with <c>departingHandle: 0</c> -- nothing is being sent anywhere, the user simply walked
    /// to another desktop -- so a reading keyed to that handle asked the OS about window zero and
    /// faithfully recorded nothing. Every arriving-path line in the supervised run read
    /// <c>departing-thread-active=0x0</c>.
    /// </para>
    /// <para>
    /// That is precisely the path the reported defect reproduces on, so the one reading built to
    /// catch it was blank exactly where it was needed. <c>fg-before</c> names the real window on
    /// BOTH paths -- on a send it equals the departing handle anyway -- so it is the correct key.
    /// </para>
    /// </remarks>
    [Fact]
    public void OnAPlainDesktopSwitch_TheThreadReadingStillNamesTheWindowThatHeldFocus()
    {
        var harness = Build();
        harness.Foreground.Handle = harness.Focused.Handle;
        harness.Foreground.ThreadActiveWindow[harness.Focused.Handle] = harness.Focused.Handle;

        harness.Executor.HandFocusToArrivingDesktop();

        var line = Assert.Single(harness.Trace.Lines, l => l.StartsWith("handover ", StringComparison.Ordinal));
        Assert.Contains(
            $"fg-before-thread-active=0x{harness.Focused.Handle:X}", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// And what the ARRIVING window's thread believes, so the pair can be compared rather than
    /// weighed one at a time.
    /// </summary>
    /// <remarks>
    /// A survivor whose own thread does NOT name itself, after an activation the OS confirmed, is a
    /// different defect from a departing thread that will not let go. Without both readings the two
    /// look identical from the log: one border on screen, one line saying <c>activated=True</c>.
    /// </remarks>
    [Fact]
    public async Task TheHandover_RecordsWhatTheSurvivorsOwnThreadBelievesToo()
    {
        var harness = Build();
        harness.Foreground.ThreadActiveWindow[harness.Survivor.Handle] = harness.Survivor.Handle;

        await Send(harness);

        var line = Assert.Single(harness.Trace.Lines, l => l.StartsWith("handover ", StringComparison.Ordinal));
        Assert.Contains(
            $"survivor-thread-active=0x{harness.Survivor.Handle:X}", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// Sending a window away is NOT an arrival, and does NOT sweep.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The user has not gone anywhere; the window has. The desktop they are looking at is the one
    /// they were already on, and its windows were never cloaked, so none of them is carrying the
    /// undelivered deactivation the sweep exists to fix. Sweeping here would activate every tile in
    /// view -- including, one moment before the shell takes it away, the very window being sent.
    /// </para>
    /// <para>
    /// <c>arriving</c> is passed EXPLICITLY rather than inferred from the departing handle being
    /// zero. The two coincide today, and relying on exactly that coincidence is what left the first
    /// thread reading blank on the arriving path.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task SendingAWindowAway_DoesNotSweep()
    {
        var harness = Build();

        await Send(harness);

        Assert.Equal([harness.Survivor.Handle], harness.ActivationOrder);
    }

    /// <summary>
    /// The arrival sweep: touch every window on the desktop, and END on the one focus is meant to
    /// land on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Aimed at a MEASURED behaviour, not a guess. Switching away from a desktop never deactivates
    /// the window that held focus there -- Windows cloaks it, it is never told it lost anything --
    /// so an application that paints its own frame from thread-local activation state keeps drawing
    /// itself active. This repository has already measured the recipe that clears it: focusing that
    /// window and then leaving it. Changing focus to something else does NOT.
    /// </para>
    /// <para>
    /// A sweep automates exactly that recipe across every window on the arriving desktop. Landing on
    /// the survivor LAST is not a detail -- it is what makes the sweep a sweep rather than a focus
    /// change, because the last window visited is the only one that does not get left.
    /// </para>
    /// <para>
    /// The order is asserted whole rather than by counting. A sweep that visited everything and
    /// finished on the wrong window would satisfy every count and strand the user's focus on
    /// whichever tile happened to be last in the tree.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheArrivalSweep_VisitsEveryWindowOnTheDesktop_AndLandsOnTheSurvivorLast()
    {
        var harness = Build();

        harness.Executor.HandFocusToArrivingDesktop();

        // The survivor search takes the tree's first leaf, so Focused is the landing spot here.
        Assert.Equal(
            [harness.Survivor.Handle, harness.Focused.Handle],
            harness.ActivationOrder);
    }

    /// <summary>The sweep says what it swept, or it is another silent thing to guess about.</summary>
    [Fact]
    public void TheArrivalSweep_RecordsEveryWindowItVisited_AndTheRungEachReported()
    {
        var harness = Build();
        harness.Survivor.NextActivation = ActivationOutcome.AttachedInput;

        harness.Executor.HandFocusToArrivingDesktop();

        var line = Assert.Single(harness.Trace.Lines, l => l.StartsWith("sweep ", StringComparison.Ordinal));
        Assert.Contains($"0x{harness.Survivor.Handle:X}:AttachedInput", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// A window the sweep could not activate does not stop the sweep, and does not stop the landing.
    /// </summary>
    /// <remarks>
    /// A refused activation mid-sweep is ordinary: the window may be protected, or Windows may
    /// simply say no. Abandoning the walk there would leave focus parked on whichever window the
    /// sweep happened to reach, which is a worse outcome than the border it set out to clear.
    /// </remarks>
    [Fact]
    public void WhenASweptWindowRefusesActivation_TheSweepCarriesOn_AndStillLands()
    {
        var harness = Build();
        harness.Survivor.FailNextActivate();

        harness.Executor.HandFocusToArrivingDesktop();

        Assert.Equal(
            [harness.Survivor.Handle, harness.Focused.Handle],
            harness.ActivationOrder);
        var line = Assert.Single(harness.Trace.Lines, l => l.StartsWith("handover ", StringComparison.Ordinal));
        Assert.Contains("activated=True", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The heart of it, read straight off the cache rather than inferred from a later chord.
    /// </summary>
    /// <remarks>
    /// The cache is SEEDED first, because that is the only state in which the defect exists: a
    /// chord run while the doomed window held the foreground writes it into <c>_focused</c>, and
    /// from then on it answers every resolution the OS foreground cannot. The foreground is then
    /// made untracked, which is exactly what the shell leaves behind once the window it named has
    /// gone to another desktop -- and that is when the cache does the damage.
    /// </remarks>
    [Fact]
    public async Task AfterTheMove_TheDepartedWindowIsNoLongerTheRememberedFocus()
    {
        var harness = Build();

        // Seeds the cache with the doomed window: it holds the foreground, so resolving names it.
        await harness.Executor.ScheduleAsync(
            new HotkeyAction(HotkeyActionKind.FocusLeft), CancellationToken.None);
        harness.Foreground.Handle = new IntPtr(0x404);
        Assert.Equal(harness.FocusedLeaf, harness.Executor.ResolveFocusedLeaf());

        harness.Foreground.Handle = harness.Focused.Handle;
        await Send(harness);
        harness.Foreground.Handle = new IntPtr(0x404);

        Assert.Equal(harness.SurvivorLeaf, harness.Executor.ResolveFocusedLeaf());
    }

    /// <summary>
    /// And with nothing left to hand it to, the honest answer is no focus at all -- not the window
    /// that just left, still reading as alive because DWM only cloaked it.
    /// </summary>
    [Fact]
    public async Task WithNothingLeftOnThisDesktop_NothingIsRememberedAsFocused()
    {
        var harness = Build(withSurvivor: false);

        await harness.Executor.ScheduleAsync(
            new HotkeyAction(HotkeyActionKind.FocusLeft), CancellationToken.None);
        harness.Foreground.Handle = new IntPtr(0x404);
        Assert.Equal(harness.FocusedLeaf, harness.Executor.ResolveFocusedLeaf());

        harness.Foreground.Handle = harness.Focused.Handle;
        await Send(harness);
        harness.Foreground.Handle = new IntPtr(0x404);

        Assert.Null(harness.Executor.ResolveFocusedLeaf());
    }

    /// <summary>
    /// A move the shell REFUSED must leave the user where they were.
    /// </summary>
    /// <remarks>
    /// The assertion changed shape when focus started leaving BEFORE the window does. Focus really
    /// has moved to the survivor by the time the shell is asked, so "nothing was activated" is no
    /// longer the honest contract and asserting it would pin an implementation that no longer
    /// exists. What must hold is the END STATE: the window never left, so the user must end up back
    /// on it. Handing them to a different tile as a souvenir of a refusal would be worse than the
    /// refusal.
    /// </remarks>
    [Fact]
    public async Task AMoveTheShellRefused_PutsTheUserBackOnTheWindowThatNeverLeft()
    {
        var harness = Build();
        harness.Desktops.MoveSucceeds = false;

        await Send(harness);

        // EXACT, not "more than zero". This is the one refusal that cannot be predicted without
        // asking the shell, so it really does cost one activation each way -- and a bound is the
        // difference between a known cost and an open-ended one.
        Assert.Equal(1, harness.Survivor.TryActivateCallCount);
        Assert.Equal(1, harness.Focused.TryActivateCallCount);

        harness.Foreground.Handle = new IntPtr(0x404);
        Assert.Equal(harness.FocusedLeaf, harness.Executor.ResolveFocusedLeaf());
    }

    /// <summary>
    /// A refusal the service can answer WITHOUT asking the shell costs no activation at all.
    /// </summary>
    /// <remarks>
    /// An unrecognised Windows build reports unsupported and every desktop operation is inert. There
    /// is nothing to hand focus off FOR, so nothing is handed off and nothing has to be undone --
    /// the same zero-activation behaviour this path had before focus started leaving early.
    /// </remarks>
    [Fact]
    public async Task AMoveTheBuildCannotDoAtAll_MovesNoFocusAtAll()
    {
        var harness = Build();
        harness.Desktops.Supported = false;
        harness.Desktops.MoveSucceeds = false;

        await Send(harness);

        Assert.Equal(0, harness.Survivor.TryActivateCallCount);
        Assert.Equal(0, harness.Focused.TryActivateCallCount);
    }

    /// <summary>
    /// The OTHER free refusal: nothing is focused, so there is no window to send.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The hand-off guard rests on TWO conditions and only one of them was driven by a fact. The
    /// untested half is the one a real desktop reaches most easily -- click the wallpaper and the
    /// foreground belongs to the shell, which reports as no trackable window at all.
    /// </para>
    /// <para>
    /// It costs nothing here for the same reason the unsupported build does: the service refuses a
    /// zero handle outright, so handing focus off first would buy an activation for a move that was
    /// never going to happen, and then a second one to undo it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AMoveWithNothingToSend_MovesNoFocusAtAll()
    {
        var harness = Build();
        harness.Foreground.Handle = 0;
        harness.Desktops.MoveSucceeds = false;

        await Send(harness);

        Assert.Equal(0, harness.Survivor.TryActivateCallCount);
        Assert.Equal(0, harness.Focused.TryActivateCallCount);
    }

    /// <summary>
    /// The survivor becomes the cache only because activation SUCCEEDED. When it fails, CosmicWin
    /// must not claim the user is somewhere they are not -- the MR-2 lesson, applied to this path.
    /// </summary>
    [Fact]
    public async Task WhenActivatingTheSurvivorFails_ItDoesNotBecomeTheRememberedFocus()
    {
        var harness = Build();
        harness.Survivor.FailNextActivate();

        await Send(harness);
        Assert.Equal(1, harness.Survivor.TryActivateCallCount);

        // Untracked foreground, so only the cache can answer -- and it must be empty rather than
        // holding a window CosmicWin never actually put the user on.
        harness.Foreground.Handle = new IntPtr(0x404);

        Assert.Null(harness.Executor.ResolveFocusedLeaf());
    }


    private sealed record SwitchHarness(
        ActionExecutor Executor,
        FakeVirtualDesktops Desktops,
        FakeForegroundWindowSource Foreground,
        RecordingWindow Here,
        RecordingWindow There,
        LeafNode HereLeaf,
        LeafNode ThereLeaf);

    /// <summary>
    /// One monitor, two desktops, one window on each -- the smallest world in which "the focused
    /// window is on the desktop the user just left" can be expressed at all.
    /// </summary>
    private static SwitchHarness BuildSwitch(bool arrivingIsEmpty = false)
    {
        var here = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var there = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var hereLeaf = new LeafNode(new WindowRef(new IntPtr(0xD1)));
        var thereLeaf = new LeafNode(new WindowRef(new IntPtr(0xD2)));

        var registry = new WindowRegistry();
        var hereWindow = new RecordingWindow(hereLeaf.Window.Handle, Rectangle.FromSize(0, 0, 1920, 1080));
        var thereWindow = new RecordingWindow(thereLeaf.Window.Handle, Rectangle.FromSize(0, 0, 1920, 1080));
        registry.Register(hereWindow, hereLeaf);
        registry.Register(thereWindow, thereLeaf);

        var display = new FakeDisplay(
            new IntPtr(1), Rectangle.FromSize(0, 0, 1920, 1080), Rectangle.FromSize(0, 0, 1920, 1080), 1.0, true);

        var desktops = new FakeVirtualDesktops { CurrentDesktopId = here, SwitchesTo = there };
        var treeManager = new TreeManager([display], display, registry)
        {
            CurrentDesktop = () => desktops.CurrentDesktopId,
        };

        treeManager.TryGetTree(here, display, out var hereTree);
        hereTree!.Root = hereLeaf;

        treeManager.TryGetTree(there, display, out var thereTree);
        thereTree!.Root = arrivingIsEmpty ? null : thereLeaf;

        var foreground = new FakeForegroundWindowSource { Handle = hereWindow.Handle };
        var executor = new ActionExecutor(hereTree, registry, foreground)
        {
            WorkArea = new Rect(0, 0, 1920, 1080),
            TreeManager = treeManager,
            VirtualDesktops = desktops,
        };

        return new SwitchHarness(
            executor, desktops, foreground, hereWindow, thereWindow, hereLeaf, thereLeaf);
    }

    private static Task Switch(SwitchHarness harness, int desktop = 2) =>
        harness.Executor
            .ScheduleAsync(new HotkeyAction(HotkeyActionKind.SwitchDesktop, desktop), CancellationToken.None)
            .AsTask();

    /// <summary>
    /// The second reported defect, and the sibling of the first: walking to another desktop left
    /// focus behind on the one just abandoned.
    /// </summary>
    /// <remarks>
    /// Activating a window on the ARRIVING desktop is the load-bearing half here, not clearing the
    /// cache. The reconciliation tick calls <c>ResolveFocusedLeaf</c> every interval and the OS
    /// foreground branch wins there -- the registry spans every desktop, so the cloaked window the
    /// user just left still resolves to a tracked leaf and would be written straight back into the
    /// cache. Only changing the real foreground settles it, because the OS is the authority.
    /// </remarks>
    [Fact]
    public async Task SwitchingDesktops_FocusesAWindowOnTheArrivingOne()
    {
        var harness = BuildSwitch();

        await Switch(harness);

        Assert.Equal(1, harness.There.TryActivateCallCount);
        Assert.Equal(0, harness.Here.TryActivateCallCount);
    }

    /// <summary>The cache follows the user, read off the public resolution rather than inferred.</summary>
    [Fact]
    public async Task AfterSwitching_TheWindowLeftBehindIsNoLongerTheRememberedFocus()
    {
        var harness = BuildSwitch();

        // Seeds the cache with the window on the desktop about to be left.
        await harness.Executor.ScheduleAsync(
            new HotkeyAction(HotkeyActionKind.FocusLeft), CancellationToken.None);
        harness.Foreground.Handle = new IntPtr(0x404);
        Assert.Equal(harness.HereLeaf, harness.Executor.ResolveFocusedLeaf());

        harness.Foreground.Handle = harness.Here.Handle;
        await Switch(harness);
        harness.Foreground.Handle = new IntPtr(0x404);

        Assert.Equal(harness.ThereLeaf, harness.Executor.ResolveFocusedLeaf());
    }

    /// <summary>
    /// An empty desktop is where the user asked to go. Nothing to focus is the honest answer, not a
    /// reason to keep pointing at the window they walked away from.
    /// </summary>
    [Fact]
    public async Task SwitchingToAnEmptyDesktop_LeavesNoFocusAtAll()
    {
        var harness = BuildSwitch(arrivingIsEmpty: true);

        await harness.Executor.ScheduleAsync(
            new HotkeyAction(HotkeyActionKind.FocusLeft), CancellationToken.None);
        harness.Foreground.Handle = new IntPtr(0x404);
        Assert.Equal(harness.HereLeaf, harness.Executor.ResolveFocusedLeaf());

        harness.Foreground.Handle = harness.Here.Handle;
        await Switch(harness);
        harness.Foreground.Handle = new IntPtr(0x404);

        Assert.Equal(0, harness.There.TryActivateCallCount);
        Assert.Null(harness.Executor.ResolveFocusedLeaf());
    }

    /// <summary>A switch the shell REFUSED is not a switch. The user never went anywhere.</summary>
    [Fact]
    public async Task ASwitchTheShellRefused_LeavesFocusExactlyWhereItWas()
    {
        var harness = BuildSwitch();
        harness.Desktops.SwitchSucceeds = false;

        await Switch(harness);

        Assert.Equal(0, harness.There.TryActivateCallCount);
        Assert.Equal(0, harness.Here.TryActivateCallCount);
    }

    /// <summary>
    /// Switching to the desktop already shown must not shuffle focus. The real service treats that
    /// as a no-op rather than paying for a desktop-change animation, and so must this.
    /// </summary>
    [Fact]
    public async Task SwitchingToTheDesktopAlreadyShown_DoesNotDisturbFocus()
    {
        var harness = BuildSwitch();
        harness.Desktops.SwitchesTo = harness.Desktops.CurrentDesktopId;

        await Switch(harness, desktop: 1);

        Assert.Equal(0, harness.Here.TryActivateCallCount);
        Assert.Equal(0, harness.There.TryActivateCallCount);
    }

    /// <summary>
    /// Without a <see cref="TreeManager"/> there is no way to name the tree the user is looking at,
    /// so no survivor can be chosen -- but dropping the stale cache is still right, and still the
    /// half that fixes the reported defect. A quiet no-op, never a throw.
    /// </summary>
    [Fact]
    public async Task WithNoTreeManagerWired_TheChordIsStillAQuietNoOp()
    {
        var registry = new WindowRegistry();
        var executor = new ActionExecutor(
            new LayoutTree(), registry, new FakeForegroundWindowSource { Handle = new IntPtr(0xABC) })
        {
            VirtualDesktops = new FakeVirtualDesktops(),
        };

        await executor.ScheduleAsync(
            new HotkeyAction(HotkeyActionKind.MoveWindowToDesktop, 2), CancellationToken.None);
    }
}
