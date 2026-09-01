using CosmicWin.Interop;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App;

/// <summary>
/// The first real production
/// caller of <see cref="TreeManager"/>. Reuses <see cref="WorkspaceSessionAdapter"/>'s extracted
/// <see cref="WorkspaceSessionAdapter.InsertWindow"/>/<see cref="WorkspaceSessionAdapter.RemoveWindow"/>/
/// <see cref="WorkspaceSessionAdapter.IsExcluded"/> statics (never a drifting second copy), but
/// resolves each window's owning monitor via <see cref="TreeManager.ResolveDisplay"/> instead of a
/// single fixed tree (MM-1), so add/remove/reflow apply only to the affected monitor's tree (MM-4's
/// isolation property).
/// </summary>
/// <remarks>
/// <see cref="ActionExecutor"/> is now <see cref="TreeManager"/>-aware
/// a hotkey mutation on a secondary monitor's focused window arranges that SAME secondary tree on
/// its own work area, so tree and screen no longer desync after Move/Resize/Toggle. Cross-monitor
/// MOVEMENT via a HOTKEY (moving a window from one monitor's tree to another's with Move/Resize/
/// Toggle) remains out of scope, per design: <see cref="CosmicWin.Layout.LayoutTree.MoveNode"/>
/// operates strictly inside <c>focused.Parent</c>. MM-5 focus fallthrough and MM-2/MM-3/MM-4's
/// live hotplug/DPI-change triggers stay unwired: no such Win32 event source exists yet in
/// <c>CosmicWin.Interop</c>.
/// </remarks>
/// <remarks>
/// (an earlier decision, supersedes 's/'s own "tree follows window" choice, never put to the
/// user): a dragged window SNAPS BACK to its tree slot on drop -- the tree is the source of truth;
/// windows move between slots/monitors only via a hotkey. A plain re-arrange of the
/// window's OWN tree undoes any drag by construction, since <see cref="TreeArranger"/> never reads
/// on-screen position -- removes 's cross-monitor re-home and 's reorder outright, rather
/// than fixing them further. Gated by <see cref="_isPaused"/> exactly like an earlier decision.
/// <para>
/// Narrowed since, on the maintainer's report, to POSITION only. Size is not a slot: a window
/// dragged bigger is asking for a boundary between two tiles to move, which the tree can express
/// exactly, and answering it with a snap-back was the tree refusing to record something it could
/// hold. The drag now goes through <see cref="TreeArranger.TryApplyUserResize"/> BEFORE the
/// reflow, so the reflow lands it; the tree stays the source of truth, it just learned this one
/// fact from the mouse.
/// </para>
/// </remarks>
/// <remarks>
/// The "evict a window that refuses repositioning"
/// guard wired into <see cref="OnWindowBoundsChanged"/> alone now lives inside the shared
/// <see cref="TreeArranger.ArrangeAndPosition"/> choke point, so <see cref="OnWindowAdded"/> (which
/// had no guard at all) is covered too -- <see cref="TreeArranger"/> has 8 total call sites across
/// this class, <see cref="ActionExecutor"/>, <see cref="TreeManager"/> and <see
/// cref="WorkspaceSessionAdapter"/>. This class keeps only a thin <c>_owners</c> cleanup line at
/// each call site: <see cref="TreeArranger"/> owns tree/registry eviction, but has no access to
/// this adapter's own private per-window display bookkeeping.
/// </remarks>
public sealed class MultiMonitorWorkspaceAdapter : IDisposable
{
    private readonly IWorkspace _workspace;
    private readonly TreeManager _treeManager;
    private readonly WindowRegistry _registry;
    private readonly Func<ExceptionList> _exceptions;
    private readonly Func<bool> _isPaused;
    private readonly Func<LeafNode?> _focusedLeaf;

    /// <summary>Handed to every <see cref="TreeArranger.ArrangeAndPosition"/> call below, so a reflow this adapter causes reaches the focus border.</summary>
    private readonly Action<IReadOnlyList<nint>>? _afterArrange;

    private readonly Dictionary<nint, IDisplay> _owners = new();

    /// <summary>
    /// How many times one window may be handed the SAME tile and fail to arrive on it before the
    /// tree gives up on it.
    /// </summary>
    /// <remarks>
    /// Generous on purpose. An application moves itself around while it starts up, and an early
    /// wobble is not a fight -- evicting on two or three would throw out windows that go on to
    /// behave perfectly. A real fighter reaches this in well under a second: the measured one
    /// issued hundreds of rounds per second, indefinitely.
    /// </remarks>
    private const int MissesBeforeGivingUp = 12;

    /// <summary>The tile each window was last offered, and how many times running it has missed it.</summary>
    private readonly Dictionary<nint, (Rect Tile, int Misses)> _misses = new();

    /// <summary>
    /// Windows the tree has given up on. Refusing re-admission is half the guard, not a detail:
    /// a fighter stays visible, trackable and un-excluded, so the next reconciliation pass would
    /// adopt it straight back and the storm would resume at the same rate.
    /// </summary>
    private readonly HashSet<nint> _givenUp = [];


    /// <summary>
    /// Which virtual desktop a window is on. Unset means "there is only one", which is how every
    /// caller that predates virtual desktops behaves.
    /// </summary>
    /// <remarks>
    /// A window must be filed under the desktop it is ACTUALLY on, which is not always the one
    /// being viewed: a window can arrive on a desktop the user is not looking at. Getting this
    /// wrong is invisible until the user switches and finds a layout that was never theirs.
    /// </remarks>
    public Func<nint, Guid>? ResolveWindowDesktop { get; set; }

    /// <summary>
    /// Which virtual desktop the USER is on -- which is not always the one the shell reports.
    /// </summary>
    /// <remarks>
    /// Deliberately a separate question from <see cref="TreeManager.CurrentDesktop"/>. A window born
    /// elsewhere can drag the view after it before CosmicWin ever hears about the window, so by the
    /// time <see cref="OnWindowAdded"/> runs the shell's live answer is already the wrong one -- it
    /// names where the arriving window took the user, not where the user was. This must answer from
    /// what was true just BEFORE the window appeared, or the redirect below has nothing to aim at.
    /// </remarks>
    public Func<Guid>? ResolveUserDesktop { get; set; }

    /// <summary>
    /// Sends a window to a desktop, reporting whether the shell actually did it. Unset -- as in every
    /// test that predates this and every build without virtual desktops -- nothing is ever redirected.
    /// </summary>
    public Func<nint, Guid, bool>? SendWindowToDesktop { get; set; }

    /// <summary>
    /// Records every out-of-band bounds change this adapter reacts to. Unset in normal runs.
    /// </summary>
    /// <remarks>
    /// A window moving on its own is invisible from inside: the adapter is TOLD a window's bounds
    /// changed and re-applies the tree's geometry, and from the user's side that is indistinguishable
    /// from CosmicWin having moved the window for no reason. This says which window, from where, to
    /// where, and whether the reflow was ours.
    /// </remarks>
    public Diagnostics.IDesktopTrace? Trace { get; set; }

    /// <param name="focusedLeaf">
    /// LE-4: the tile a newly arriving window splits. Mandatory rather than optional -- a dropped
    /// focus source does not fail, it silently reverts to appending every window to the end of the
    /// row, which is exactly the defect this parameter exists to close.
    /// </param>
    public MultiMonitorWorkspaceAdapter(
        IWorkspace workspace, TreeManager treeManager, WindowRegistry registry,
        Func<ExceptionList> exceptions, Func<bool> isPaused, Func<LeafNode?> focusedLeaf,
        Action<IReadOnlyList<nint>>? afterArrange = null)
    {
        _focusedLeaf = focusedLeaf;
        _workspace = workspace;
        _treeManager = treeManager;
        _registry = registry;
        _exceptions = exceptions;
        _isPaused = isPaused;
        _afterArrange = afterArrange;

        _workspace.WindowAdded += OnWindowAdded;
        _workspace.WindowRemoved += OnWindowRemoved;
        _workspace.WindowBoundsChanged += OnWindowBoundsChanged;
    }

    private void OnWindowAdded(object? sender, WindowEventArgs e)
    {
        if (_isPaused())
        {
            return;
        }

        var window = e.Window;
        if (WorkspaceSessionAdapter.IsExcluded(window, _exceptions()))
        {
            return;
        }

        // Given up on, and staying that way for as long as this handle lives. See _givenUp.
        if (_givenUp.Contains(window.Handle))
        {
            return;
        }

        // ONE HWND, ONE LEAF. The shell announces the same window as added more than once --
        // measured on real hardware, three times for a single Photoshop window in one session,
        // while every ordinary window was announced exactly once.
        //
        // Every announcement used to build a NEW leaf. WindowRegistry.Register only replaces the
        // mapping, so the earlier leaf stayed in the tree with nothing able to reach it:
        // TreeArranger walks the TREE and found both, handing one handle two rectangles per pass,
        // and each reposition raised the bounds-changed event that drove the next pass. Recorded:
        // 17,467 reflows of one handle in 57 seconds, ~306 a second, a 2.5 MB trace file, and the
        // window itself split the screen with a copy of itself. A close afterwards detached only
        // the registered leaf, so the other kept its slot for as long as the app ran -- the empty
        // tile left behind by a window that no longer exists.
        //
        // Verified against the TREE, never the registry on its own. A registry entry can outlive
        // the tree it pointed into, and refusing on a stale one would drop a window that really is
        // new -- nothing else would ever add it. `_owners` names the display to ask, exactly as
        // OnWindowRemoved already asks it.
        if (_owners.TryGetValue(window.Handle, out var owner)
            && _registry.TryGetLeaf(window.Handle, out var held) && held is not null
            && _treeManager.TryGetTreeHolding(owner, held, out _))
        {
            // Traced rather than dropped in silence: this path doing nothing is indistinguishable
            // from the event never arriving, and telling those two apart is the whole reason the
            // duplicate took a supervised session to find.
            Trace?.Record(
                $"duplicate add ignored hwnd=0x{window.Handle:X} class={window.ClassName} " +
                $"proc={window.ProcessName} -- already a leaf on display 0x{owner.Handle:X}");
            return;
        }

        var display = _treeManager.ResolveDisplay(window.Bounds);
        if (!_treeManager.TryGetTree(display, out var visible) || visible is null)
        {
            return;
        }

        // UNKNOWN means the desktop being viewed, never the empty one. The shell answers Guid.Empty
        // for a window it will not place -- mid-creation, or minimized -- and taking that literally
        // filed every arriving window under a desktop nobody was looking at, so nothing was ever
        // arranged and CosmicWin stopped tiling outright (measured). Guessing "here" can
        // only be wrong about a window the user cannot see anyway; guessing "nowhere" loses windows.
        var named = ResolveWindowDesktop?.Invoke(window.Handle) ?? Guid.Empty;

        // A window opens where the USER is. Windows decides where a new window is born, and an
        // application that already owns one elsewhere can have the next born beside it -- measured
        // in the desktop trace, which showed the user switch to desktop 2, launch a browser, and end
        // up on desktop 1 with no switch of ours in between. Filing it faithfully was still the
        // wrong answer: it recorded the shell's decision instead of overruling it.
        //
        // Only ever on a NAMED desktop that is not the user's. Empty means the shell would not say,
        // which it answers for any window merely mid-creation -- moving windows on that would be
        // moving them on a guess.
        // A BIRTH only. An adopted window made no decision to overrule -- it has been sitting where
        // the user left it, and this redirect used to drag it away the moment CosmicWin first saw
        // it. Reported: restart with windows on more than one desktop, then move between them, and
        // the other desktops empty themselves into the one CosmicWin believes the user is on.
        //
        // Guaranteed rather than unlucky, and worth spelling out because it reads like a race that
        // a smaller window would fix. IsTrackable rejects cloaked windows and DWM cloaks every
        // window on a desktop nobody is looking at, so a starting CosmicWin can only adopt the
        // desktop in view; every other desktop is adopted later, at the instant the user walks over
        // and its windows uncloak. The reconciliation tick polls BEFORE it refreshes which desktop
        // the user is on, so at that instant `user` still names the desktop they just left. The
        // window that had never moved was therefore moved -- to a desktop the user was no longer on.
        //
        // Reordering the tick would shrink that window without closing it, because the ordering is
        // the symptom: two different facts were arriving on one event with nothing to tell them
        // apart. WindowArrival is what tells them apart.
        var redirected = false;
        var user = ResolveUserDesktop?.Invoke() ?? Guid.Empty;
        if (e.Arrival == WindowArrival.Created
            && SendWindowToDesktop is { } send && named != Guid.Empty && user != Guid.Empty && named != user)
        {
            // A refused move leaves `named` alone on purpose. Filing it where we WANTED it would
            // describe a desktop the window is not on -- the same lie the empty desktop id already
            // taught this code not to tell.
            redirected = send(window.Handle, user);
            if (redirected)
            {
                named = user;
            }
        }

        var tree = visible;
        if (named != Guid.Empty)
        {
            if (!_treeManager.TryGetTree(named, display, out var owning) || owning is null)
            {
                return;
            }

            tree = owning;
        }

        var workArea = WorkAreaResolver.Resolve(display);

        // LE-4 splits the FOCUSED tile, but only when the focused window is on the same tree. A
        // window arriving on a desktop the user is not viewing has no focused tile to split there.
        // A redirected window counts as arriving on the user's own tree even when the shell has
        // momentarily taken the view elsewhere -- that view is about to be put back, and the tile
        // the user was working in is the one this window should split. InsertWindow re-checks that
        // the leaf really hangs off this tree, so a stale focus cannot place a window wrongly.
        var focused = redirected || ReferenceEquals(visible, tree) ? _focusedLeaf() : null;

        WorkspaceSessionAdapter.InsertWindow(tree, _registry, workArea, window, focused);
        _owners[window.Handle] = display;

        // Laid out whether or not its desktop is on screen. A hidden window accepts a position --
        // measured -- and doing it now is what makes the desktop already correct when the user
        // arrives, instead of correcting itself in front of them.
        var beforeArrange = window.Bounds;
        TreeArranger.ArrangeAndPosition(tree, _registry, workArea, _afterArrange);

        Trace?.Record(
            $"added hwnd=0x{window.Handle:X} class={window.ClassName} proc={window.ProcessName} " +
            $"[L={beforeArrange.Left} T={beforeArrange.Top} " +
            $"W={beforeArrange.Width} H={beforeArrange.Height}] -> " +
            $"[L={window.Bounds.Left} T={window.Bounds.Top} " +
            $"W={window.Bounds.Width} H={window.Bounds.Height}] redirected={redirected}");

        // The choke point above evicts a window that fails ITS OWN first positioning
        // attempt (e.g. a protected window that never accepts a reposition) from the tree and
        // registry -- clean up this adapter's own per-window bookkeeping to match, since
        // TreeArranger has no access to it.
        if (!window.CanReposition)
        {
            _owners.Remove(window.Handle);
        }
    }

    /// <summary>
    /// Brings both trees in line after the SHELL has already moved a window to another desktop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The move itself only changes where Windows draws the window; without this the layouts never
    /// hear about it. The window kept its slot on the desktop it left -- a hole nothing was drawn
    /// into -- and never joined the one it arrived at. Reported immediately after the move landed.
    /// </para>
    /// <para>
    /// Both halves are deliberately the ORDINARY paths, not special cases. Leaving reuses the same
    /// removal and reflow a CLOSE does, so the survivors reclaim the space the same way. Arriving
    /// reuses the same insertion a NEW WINDOW does, so it takes its place among whatever is already
    /// there. A move is a close and an open, in that order.
    /// </para>
    /// <para>
    /// The destination is read back from the shell rather than passed in: it has already recorded
    /// the move, which makes it the ground truth. An unknown answer leaves BOTH trees untouched --
    /// half-applying this would be worse than not applying it, since the window would vanish from
    /// one layout without appearing in the other.
    /// </para>
    /// </remarks>
    public void RehomeToDesktop(nint windowHandle)
    {
        if (_isPaused() || !_owners.TryGetValue(windowHandle, out var display))
        {
            return;
        }

        var target = ResolveWindowDesktop?.Invoke(windowHandle) ?? Guid.Empty;
        if (target == Guid.Empty
            || !_registry.TryGetWindow(windowHandle, out var window) || window is null
            || !_treeManager.TryGetTree(target, display, out var arriving) || arriving is null)
        {
            return;
        }

        var workArea = WorkAreaResolver.Resolve(display);

        // Leaving: exactly a close, from whichever tree ACTUALLY holds it. Assuming the visible one
        // only holds for a move CosmicWin issued itself; the shell reassigns windows on its own
        // when a desktop closes, and those are filed under a desktop that no longer exists.
        if (!_registry.TryGetLeaf(windowHandle, out var leaf) || leaf is null)
        {
            return;
        }

        if (_treeManager.TryGetTreeHolding(display, leaf, out var leaving) && leaving is not null)
        {
            // Already where it belongs: say nothing and change nothing, or the reconciliation pass
            // would re-lay every window every time it ran.
            if (ReferenceEquals(leaving, arriving))
            {
                return;
            }

            if (WorkspaceSessionAdapter.RemoveWindow(leaving, _registry, windowHandle))
            {
                TreeArranger.ArrangeAndPosition(leaving, _registry, workArea, _afterArrange);
            }
        }

        // Arriving: exactly a new window, AND laid out immediately. Deferring it until the user
        // walks over showed them a loose, wrongly-sized window that then snapped into place.
        // Measured before relying on it, because a refused SetWindowPos latches CanReposition to
        // false and TreeArranger would EVICT the leaf: a window on a desktop nobody is looking at
        // accepts a position exactly, wanted [120,140,700x480] read back identical.
        WorkspaceSessionAdapter.InsertWindow(arriving, _registry, workArea, window, focused: null);
        TreeArranger.ArrangeAndPosition(arriving, _registry, workArea, _afterArrange);
        _owners[windowHandle] = display;
    }

    /// <summary>
    /// Re-files every tracked window that the SHELL moved without telling us.
    /// </summary>
    /// <remarks>
    /// Reported from real use: deleting a desktop handed its windows to another one, where they sat
    /// untiled. Every other rehome here is triggered by a chord CosmicWin issued, and a window
    /// manager that only learns about moves it made is blind to a closed desktop, to a Task View
    /// drag, and to anything else the shell decides on its own. So this asks rather than waiting to
    /// be told. A window already filed correctly costs one lookup and changes nothing.
    /// </remarks>
    public void ReconcileDesktops()
    {
        if (_isPaused() || ResolveWindowDesktop is null)
        {
            return;
        }

        // Copied first: rehoming mutates _owners, and enumerating a dictionary while it changes
        // throws.
        foreach (var handle in _owners.Keys.ToArray())
        {
            RehomeToDesktop(handle);
        }
    }

    /// <summary>
    /// Whether <paramref name="handle"/> has now missed the SAME tile too many times running.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tile has to match too, or this would count an ordinary busy desktop as a fight: every
    /// time a neighbour opens or closes, every survivor is handed a DIFFERENT tile and arrives at
    /// it a moment later, which is convergence, not failure. Only the same target missed over and
    /// over says the window is never going to get there.
    /// </para>
    /// <para>
    /// A leaf with no arranged geometry yet has been offered nothing to miss, so it cannot be
    /// failing to reach it.
    /// </para>
    /// </remarks>
    private bool HasGivenUpOn(nint handle, Rectangle arrivedAt)
    {
        if (!_registry.TryGetLeaf(handle, out var leaf) || leaf is null)
        {
            return false;
        }

        var tile = TreeArranger.TileOf(leaf);
        if (tile.Width <= 0 || tile.Height <= 0)
        {
            return false;
        }

        if (arrivedAt.Left == tile.X && arrivedAt.Top == tile.Y
            && arrivedAt.Width == tile.Width && arrivedAt.Height == tile.Height)
        {
            // Landed. Whatever it did before this is forgiven -- an application that wobbles while
            // it starts up and then behaves is not a fighter.
            _misses.Remove(handle);
            return false;
        }

        var misses = _misses.TryGetValue(handle, out var seen) && seen.Tile.Equals(tile) ? seen.Misses + 1 : 1;
        _misses[handle] = (tile, misses);
        return misses >= MissesBeforeGivingUp;
    }

    private void OnWindowRemoved(object? sender, WindowEventArgs e)
    {
        var handle = e.Window.Handle;
        if (!_owners.TryGetValue(handle, out var display) ||
            !_treeManager.TryGetTree(display, out var tree) || tree is null)
        {
            return;
        }

        _owners.Remove(handle);

        // Forgotten with the window, because Windows reuses HWND values: a handle held against a
        // future window would refuse one that never did anything wrong.
        _misses.Remove(handle);
        _givenUp.Remove(handle);

        if (!WorkspaceSessionAdapter.RemoveWindow(tree, _registry, handle))
        {
            return;
        }

        // Re-proven for this TreeManager-routed
        // adapter: removal itself always happens; only the reflow of the AFFECTED monitor's tree
        // is skipped while paused, and never fired retroactively on resume.
        if (_isPaused())
        {
            return;
        }

        Trace?.Record(
            $"removed hwnd=0x{handle:X} class={e.Window.ClassName} proc={e.Window.ProcessName} " +
            $"-- survivors reflowed");

        TreeArranger.ArrangeAndPosition(tree, _registry, WorkAreaResolver.Resolve(display), _afterArrange);
    }

    /// <summary>
    /// Decision #80: snaps an already-tracked window back to its tree slot after any
    /// out-of-band move. No-op while paused or for an untracked/excluded window. Re-arranging the
    /// UNCHANGED tree re-applies the same geometry, undoing the move on screen, cross-monitor
    /// included (returns to its ORIGINAL slot).
    /// <para>
    /// One case is no longer out-of-band: a hand-RESIZE the user finished with the mouse
    /// (<see cref="WindowEventArgs.IsUserGesture"/>) is written into the tree first, so the reflow
    /// keeps the size that was dragged. Position is untouched -- a window still cannot leave its
    /// slot by being dragged -- and a drag on an axis where the window has no neighbour still
    /// snaps back, because there is no boundary there to move.
    /// </para>
    /// </summary>
    /// <remarks>
    /// The snap-back attempt above can itself fail -- a window
    /// whose <see cref="IWindow.SetPosition"/> refuses to move flips <see
    /// cref="IWindow.CanReposition"/> to <c>false</c> permanently (one-way, never self-heals per its
    /// documented contract). Left alone, that window keeps the drag forever while the tree still
    /// treats it as being in its old slot -- desyncing tree order from screen order for good (the
    /// measured defect: a dragged-past sibling becomes the wrong `FocusRight` target). Decided
    /// reading for a window ALREADY in the tree that starts refusing repositioning: treat it
    /// exactly like a WE-1 exclusion -- evict it from the tree/registry (untileable, left floating
    /// where it is) and reflow the remaining siblings into the space it vacated, mirroring the
    /// design threat-matrix row "Cross-process window manipulation" (leaf marked untileable, never
    /// retried in a loop).: the actual eviction now happens INSIDE <see
    /// cref="TreeArranger.ArrangeAndPosition"/> (the shared choke point, closes) -- this
    /// method only cleans up its own <c>_owners</c> entry afterward.
    /// </remarks>
    private void OnWindowBoundsChanged(object? sender, WindowEventArgs e)
    {
        if (_isPaused())
        {
            return;
        }

        var window = e.Window;
        var handle = window.Handle;

        // WE-1 exclusion is otherwise decided once, in OnWindowAdded, and never revisited. That
        // holds for every permanent trait it tests, but NOT for WS_MINIMIZE, which Windows sets and
        // clears as the user minimises and restores. Both transitions move the window -- minimising
        // parks it at (-32000,-32000) -- so both arrive here, which makes this the one place the
        // verdict can be kept honest.
        var excluded = WorkspaceSessionAdapter.IsExcluded(window, _exceptions());

        if (!_owners.TryGetValue(handle, out var display))
        {
            // Untracked and no longer excluded: it just became tileable (a restore). Route it
            // through the ordinary add path rather than a second, drifting copy of it.
            if (!excluded)
            {
                OnWindowAdded(sender, e);
            }

            return;
        }

        if (!_treeManager.TryGetTree(display, out var tree) || tree is null)
        {
            return;
        }

        if (excluded)
        {
            // Tracked but no longer tileable (a minimise). Measured: left in the tree it
            // keeps a full tile while drawing nothing, which is what made one visible window occupy
            // only half the screen. Remove it and reflow the survivors into the space.
            _owners.Remove(handle);
            if (WorkspaceSessionAdapter.RemoveWindow(tree, _registry, handle))
            {
                TreeArranger.ArrangeAndPosition(tree, _registry, WorkAreaResolver.Resolve(display), _afterArrange);
            }

            return;
        }

        var before = window.Bounds;

        // The user's own hand-resize is the one bounds change that carries an INTENT about the
        // layout, so it is written into the tree first and the reflow below then lands it. Every
        // other bounds change still snaps back untouched, per an earlier decision -- and so does the
        // part of this one the tree cannot express: a drag on an axis where the window has no
        // neighbour has no boundary to move, and ApplyEdgeDrag leaves it alone rather than
        // approximating one.
        // A MAXIMISED window is not asking for a boundary to move, whatever its rectangle says.
        // The shape rule in ApplyEdgeDrag catches a maximise that travels on both edges, but a
        // window already flush against the work-area corner leaves that edge anchored and slips
        // through -- and whether it is flush depends on the gap, which is not something the
        // correctness of this may rest on. The state bit does not care about geometry at all.
        var maximized = (window.Style & Layout.Filters.WindowStyleFlags.Maximized) != 0;

        if (e.IsUserGesture && !maximized &&
            _registry.TryGetLeaf(handle, out var dragged) && dragged is not null)
        {
            var tile = TreeArranger.TileOf(dragged);
            var dropped = window.Bounds;

            // A gesture that changed the LENGTH moved a boundary; one that did not moved the whole
            // window. They are different questions and only one of them is a resize, so they are
            // separated here rather than inferred from a resize that declined to apply.
            var resized = dropped.Width != tile.Width || dropped.Height != tile.Height;
            var outcome = resized
                ? TreeArranger.TryApplyUserResize(dragged, dropped) ? "tree resized" : "nothing to resize on either axis"
                : TrySwapOnDrop(display, dragged, dropped);

            // The tile it was measured AGAINST, not just the verdict. The one thing no test can
            // answer is whether a real Win32 drop lines up with the tile actually placed once the
            // gap is on -- a drag that reads as a few phantom pixels of movement is indistinguishable
            // from one the user really made, and both come out of here as "resized".
            var slot = dragged.LastGeometry;
            Trace?.Record(
                $"drag hwnd=0x{handle:X} class={window.ClassName} " +
                $"slot=[X={slot.X} Y={slot.Y} W={slot.Width} H={slot.Height}] gap={TreeArranger.Gap} " +
                $"dropped=[L={dropped.Left} T={dropped.Top} " +
                $"W={dropped.Width} H={dropped.Height}] -- {outcome}");
        }

        // A window that will not stay where it is put is given up on rather than fought forever.
        // Measured with Clipchamp: its InputNonClientPointerSource accepted every reposition,
        // reported itself off the right edge of the desktop every time, and produced 31,759 reflows
        // of one handle in two minutes. Photoshop produced the identical shape from a `Button`.
        //
        // Non-convergence is the signature, deliberately -- not a size, not a class name, not a
        // refusal. CanReposition already catches a window that says no; this one says yes and
        // drifts, which is indistinguishable from compliance at the moment of the call. And a list
        // of class names is always one release behind, while this catches the next one nobody has
        // met yet.
        //
        // A user's own drag is exempt: it is SUPPOSED to leave the window off its tile, and the
        // block above has just written that intent into the tree.
        if (!e.IsUserGesture && HasGivenUpOn(handle, before))
        {
            _misses.Remove(handle);
            _givenUp.Add(handle);
            _owners.Remove(handle);

            Trace?.Record(
                $"gave up hwnd=0x{handle:X} class={window.ClassName} proc={window.ProcessName} " +
                $"-- never reached its tile in {MissesBeforeGivingUp} attempts; evicted and refused");

            if (WorkspaceSessionAdapter.RemoveWindow(tree, _registry, handle))
            {
                TreeArranger.ArrangeAndPosition(tree, _registry, WorkAreaResolver.Resolve(display), _afterArrange);
            }

            return;
        }

        TreeArranger.ArrangeAndPosition(tree, _registry, WorkAreaResolver.Resolve(display), _afterArrange);

        if (before != window.Bounds)
        {
            Trace?.Record(
                $"reflow hwnd=0x{handle:X} class={window.ClassName} proc={window.ProcessName} " +
                $"[L={before.Left} T={before.Top} W={before.Width} H={before.Height}] -> " +
                $"[L={window.Bounds.Left} T={window.Bounds.Top} " +
                $"W={window.Bounds.Width} H={window.Bounds.Height}]");
        }

        if (!window.CanReposition)
        {
            _owners.Remove(handle);
        }
    }

    /// <summary>
    /// Exchanges the dropped window's slot with whichever tile it was dropped ONTO. Reports what
    /// happened, for the trace.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Narrows an earlier decision a second time. That rule made a dragged window snap back
    /// wholesale; the size half was already given to the user, and this is the position half. It
    /// still applies to every bounds change that is NOT the user's own drag -- an app moving
    /// itself, a restore, a shell nudge -- which is why nothing about those changed.
    /// </para>
    /// <para>
    /// SWAP rather than insert-at-the-drop-edge, chosen by the maintainer. It cannot leave the tree
    /// in a shape nobody asked for: no group is created, none is emptied, no size moves. The
    /// tempting richer version -- splitting the target on the edge you dropped against -- needs a
    /// prune of the group the window vacated and a new "which edge" concept, and is a separate
    /// decision rather than a bigger version of this one.
    /// </para>
    /// <para>
    /// Aimed with the dropped window's CENTRE. The cursor is what the user actually aims with, but
    /// reading it needs interop this does not have yet, and a swap targets a whole tile rather than
    /// an edge of one, so the two agree for anything but a large window dropped on a small tile.
    /// Worth revisiting if that case is ever felt.
    /// </para>
    /// </remarks>
    private string TrySwapOnDrop(IDisplay display, LeafNode dragged, Rectangle dropped)
    {
        var target = _treeManager.LeafAt(
            display,
            dropped.Left + (dropped.Width / 2),
            dropped.Top + (dropped.Height / 2));

        // Dropped on nothing this display holds -- most often because the drag left the monitor.
        // The window goes back to its slot, exactly as it did before any of this existed.
        if (target is null)
        {
            return "dropped outside every tile";
        }

        if (ReferenceEquals(target, dragged))
        {
            return "dropped on its own tile";
        }

        return LayoutTree.SwapLeaves(dragged, target)
            ? $"swapped with 0x{target.Window.Handle:X}"
            : "swap refused";
    }

    public void Dispose()
    {
        _workspace.WindowAdded -= OnWindowAdded;
        _workspace.WindowRemoved -= OnWindowRemoved;
        _workspace.WindowBoundsChanged -= OnWindowBoundsChanged;
    }
}
