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

        public nint GetForegroundHandle() => Handle;
    }

    private sealed record Harness(
        ActionExecutor Executor,
        FakeVirtualDesktops Desktops,
        FakeForegroundWindowSource Foreground,
        RecordingWindow Focused,
        RecordingWindow Survivor,
        LayoutTree Tree,
        LeafNode FocusedLeaf,
        LeafNode SurvivorLeaf);

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
        var executor = new ActionExecutor(managed, registry, foreground)
        {
            WorkArea = new Rect(0, 0, 1920, 1080),
            TreeManager = treeManager,
            VirtualDesktops = desktops,
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
            executor, desktops, foreground, focused, survivor, managed, focusedLeaf, survivorLeaf);
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
