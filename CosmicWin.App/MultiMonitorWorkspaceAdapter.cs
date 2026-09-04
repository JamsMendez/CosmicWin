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

    /// <summary>The tile each window was last offered, WHERE it went instead, and how many times running it has missed.</summary>
    /// <remarks>
    /// The landing is kept, not just the count, because the two failures this guard has to tell
    /// apart look identical from a count alone. See <see cref="Judge"/>.
    /// </remarks>
    private readonly Dictionary<nint, (Rect Tile, Rectangle LandedAt, int Misses)> _misses = new();

    /// <summary>
    /// The size a window has PROVEN it will not go under, measured from its own behaviour.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured rather than asked for. Windows only reveals a minimum through
    /// <c>WM_GETMINMAXINFO</c>, which is readable only by SENDING the message -- and a send blocks
    /// the calling thread until the target answers, which is the freeze this codebase already
    /// refuses to risk in <c>TryClose</c>. The window demonstrates the same number for free.
    /// </para>
    /// <para>
    /// Unlike <see cref="_givenUp"/> this is not a refusal. It is a fact about the window that the
    /// next tile is measured against, and a window whose slot grows big enough is tiled normally.
    /// It self-corrects upward, since a floor that turns out to be higher is re-measured the next
    /// time the window misses; a floor that LOWERS goes unnoticed until the window is announced
    /// again, which is a staleness worth far less than a send that can hang the focus border.
    /// </para>
    /// </remarks>
    private readonly Dictionary<nint, (int Width, int Height, IDisplay LastSeenOn)> _minimumSize = new();

    /// <summary>
    /// Windows already known to stop short of filling their tile, so the fact is reported once
    /// instead of on every pass that re-observes it.
    /// </summary>
    private readonly HashSet<nint> _clampsInsideItsTile = [];

    /// <summary>
    /// Windows the tree has given up on. Refusing re-admission is half the guard, not a detail:
    /// a fighter stays visible, trackable and un-excluded, so the next reconciliation pass would
    /// adopt it straight back and the storm would resume at the same rate.
    /// </summary>
    private readonly HashSet<nint> _givenUp = [];

    /// <summary>Handles whose admission is already in progress on this call stack.</summary>
    private readonly HashSet<nint> _arriving = [];

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

    /// <summary>
    /// Serialises announcements of one handle against ITSELF, then does the work.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This handler re-enters. Measured on real hardware with the guard's own state recorded on
    /// every announcement, Clipchamp opening:
    /// </para>
    /// <code>
    /// 48.1280  guard state hwnd=0x40944 owner=False leaf=False givenUp=False
    /// 48.1292  guard state hwnd=0x40944 owner=False leaf=False givenUp=False
    /// 48.1934  added hwnd=0x40944 ...
    /// 48.2268  added hwnd=0x40944 ...
    /// </code>
    /// <para>
    /// The <c>added</c> line is written at the END of the work. Sequential processing would read
    /// guard, added, guard, added; both guards first and both additions after says the two calls
    /// were inside at the same time, 1.2 ms apart, on one thread. Something between entry and the
    /// bookkeeping pumps, and the shell delivers the next announcement into that gap.
    /// </para>
    /// <para>
    /// Which is why the duplicate-leaf guard below never fired once in production: it asks whether
    /// the handle is already an owned, registered leaf, and the code that makes it so runs later in
    /// the same method. The second call reached the question before the first had answered it, so
    /// both saw nothing and both built a leaf. Eviction then removes ONE leaf per give-up, so the
    /// surplus stayed -- orphans that keep their tiles and that the focus walk keeps electing.
    /// </para>
    /// <para>
    /// Claimed by HANDLE rather than by a lock, deliberately. A lock would serialise a re-entrant
    /// call on one thread into a deadlock or, if reentrant, change nothing. The question here is
    /// not "are two threads inside" but "is this handle already being admitted", and refusing the
    /// second answer is correct however the re-entry got in. The claim is released in a
    /// <c>finally</c> so a throw cannot leave a handle permanently unaddable.
    /// </para>
    /// </remarks>
    private void OnWindowAdded(object? sender, WindowEventArgs e)
    {
        if (!_arriving.Add(e.Window.Handle))
        {
            Trace?.Record(
                $"re-entrant add ignored hwnd=0x{e.Window.Handle:X} class={e.Window.ClassName} " +
                $"proc={e.Window.ProcessName} -- already being admitted");
            return;
        }

        try
        {
            AddWindow(e);
        }
        finally
        {
            _arriving.Remove(e.Window.Handle);
        }
    }

    private void AddWindow(WindowEventArgs e)
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
        // WHICH of the three failed, when one does. Measured on real hardware: Clipchamp's chrome
        // was announced four times against three give-ups, leaving one leaf nothing would ever
        // remove -- and the trace carried NOT ONE `duplicate add ignored` line, so this guard never
        // fired. Three conditions can refuse and the record only ever said "refused", which is the
        // one thing that did not happen. Replaying the measured order against the test harness
        // reproduces nothing, so the difference lives in state this line is about to name.
        var hasOwner = _owners.TryGetValue(window.Handle, out var owner);
        var hasLeaf = _registry.TryGetLeaf(window.Handle, out var held) && held is not null;
        var heldInATree = hasOwner && hasLeaf && _treeManager.TryGetTreeHolding(owner, held!, out _);
        if (!heldInATree)
        {
            // Every state this guard saw, not just the partial ones. The first cut of this line
            // recorded partial state only and stayed silent through three reproductions, which is
            // itself the finding: on the duplicate announcement the adapter remembers NOTHING about
            // the handle, so there is no state for the guard to match and refusing was never
            // possible. givenUp rides along because it refused nothing either, on a handle it had
            // been given moments earlier.
            Trace?.Record(
                $"guard state hwnd=0x{window.Handle:X} class={window.ClassName} " +
                $"proc={window.ProcessName} owner={hasOwner} leaf={hasLeaf} " +
                $"givenUp={_givenUp.Contains(window.Handle)}");
        }

        if (heldInATree)
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

        // A window whose floor is already on record is measured against the tile this tree WOULD
        // hand it, and turned away BEFORE anything is moved. Arranging is arithmetic; positioning
        // is what the desktop sees and what the workspace files as the window's bounds.
        //
        // The old order did both and then undid them, and that is not merely wasteful -- it is
        // self-sustaining. Measured on hardware: the ask the window could not honour was filed as
        // its bounds, the window stayed its own larger size, and the next reconciliation pass read
        // the difference as a bounds change and routed it straight back into admission. Every two
        // seconds, forever, each pass reflowing the whole display twice to re-derive a number
        // already written down.
        //
        // Admission is still retried FREELY -- this is the cheap half of it, not a refusal.
        if (DoesNotFitItsFloor(tree, workArea, window.Handle))
        {
            if (TryRegroupToFit(tree, workArea, window.Handle))
            {
                Trace?.Record(
                    $"regrouped hwnd=0x{window.Handle:X} class={window.ClassName} " +
                    $"proc={window.ProcessName} -- given a branch of its own so " +
                    $"{Describe(_minimumSize[window.Handle])} fits");
            }
            else
            {
                Trace?.Record(
                    $"still too small hwnd=0x{window.Handle:X} class={window.ClassName} " +
                    $"proc={window.ProcessName} -- needs {Describe(_minimumSize[window.Handle])}; left untiled");

                Untile(window.Handle, tree, display);
                return;
            }
        }

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
    /// Offers the tree again to every parked window, wherever it is parked. Called once per chord.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A chord that reshapes the layout -- an orientation toggle, a resize, a move -- reaches this
    /// adapter through NO event at all. The reflow moves windows through <see cref="TreeArranger"/>
    /// and <c>Win32Window.SetPosition</c> writes the asked-for rectangle straight into the cached
    /// bounds, so by the time the WinEvent lands <c>Win32Workspace.UpdateBounds</c> compares the
    /// rectangle against itself and reports no change. A compliant window moving is silent, which
    /// is the same reason the focus border needs its own callback rather than a bounds event.
    /// </para>
    /// <para>
    /// Reported from real use: Alt+O stacks the group and NVIDIA Broadcast is untiled for its
    /// floor -- correct. Alt+O again puts the columns back, which clears that floor, and nothing
    /// asked. The first cut of the fix hung on WindowBoundsChanged and changed NOTHING on hardware;
    /// the trace carried not one <c>guard state</c> line for the parked window. It passed headless
    /// only because the test raised the event by hand.
    /// </para>
    /// <para>
    /// The displays come from the parked windows themselves rather than from a monitor list: a
    /// window with a floor on record remembers where it was last seen, and a display with nothing
    /// parked on it has nothing to ask.
    /// </para>
    /// <para>
    /// Terminates. Admission either tiles the window or leaves it untiled without touching it, and
    /// neither path re-enters here -- this is called from the executor's post-chord hook, which
    /// nothing in admission reaches.
    /// </para>
    /// </remarks>
    public void RetryParkedWindows()
    {
        foreach (var display in _minimumSize.Values.Select(parked => parked.LastSeenOn).Distinct().ToArray())
        {
            RetryParkedOn(display);
        }
    }

    /// <summary>
    /// Offers the tree again to every window parked on <paramref name="display"/> for not fitting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this, "untiled until a tile that fits exists" was only ever true for a window the
    /// user happened to touch. Every other route back into the tree runs through a bounds change on
    /// the parked window itself, and a window floating where nobody is touching it produces none --
    /// so closing its neighbour freed exactly the space it needed and nothing went looking.
    /// </para>
    /// <para>
    /// Only where a slot can have GROWN, which is a window LEAVING. Admitting one only ever divides
    /// the space further, so retrying there would be work that cannot succeed.
    /// </para>
    /// <para>
    /// Asking is not forgiving: this routes through ordinary admission, which re-measures the floor
    /// against the tile actually handed out and parks the window straight back if it still does not
    /// fit. Announced as an ADOPTION rather than a birth -- the window has been sitting there all
    /// along, and a birth carries a desktop-redirect decision that would move it.
    /// </para>
    /// </remarks>
    private void RetryParkedOn(IDisplay display)
    {
        // DERIVED rather than tracked: a window with a measured floor that this adapter no longer
        // owns is a window that was untiled, because owning one is exactly what being in a tree
        // means here. A second dictionary saying the same thing would need keeping in step at four
        // call sites, and two of those lines turned out to be unreachable invariants no test could
        // reach -- state that cannot be observed being wrong is state worth not having.
        //
        // Materialised before the loop: admission writes to _minimumSize and _owners on either
        // outcome.
        var waiting = _minimumSize
            .Where(entry => ReferenceEquals(entry.Value.LastSeenOn, display) && !_owners.ContainsKey(entry.Key))
            .Select(entry => entry.Key)
            .ToArray();

        foreach (var handle in waiting)
        {
            if (_workspace.Snapshot.FirstOrDefault(open => open.Handle == handle) is { } window)
            {
                OnWindowAdded(this, new WindowEventArgs(window, arrival: WindowArrival.Adopted));
            }
        }
    }

    /// <summary>
    /// Reshapes <paramref name="tree"/> so <paramref name="handle"/> stands beside its former
    /// siblings instead of among them, reporting whether that actually made its tile fit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The third answer. A window asking for more than its share used to leave the tree or force
    /// everyone else to give way; a branch of its own costs neither. <c>[A, B, C]</c> stacked
    /// becomes <c>[[A, C] stacked, B] side by side</c>: the neighbours keep the axis the chord
    /// asked for and B gets the whole of the one it was failing on.
    /// </para>
    /// <para>
    /// MEASURED, never assumed. The trade buys one axis and sells half the other, so a window short
    /// of BOTH can come out no better off. The tree is reshaped, the tiles are computed without
    /// moving anything, and the floor is checked against the result -- only then is any of it kept.
    /// </para>
    /// <para>
    /// A refusal costs nothing to undo. The caller untiles the window, which collapses the branch
    /// built for it and leaves the neighbours exactly where the chord put them.
    /// </para>
    /// <para>
    /// It cannot nest without bound. Extracting a leaf that is ALREADY standing beside a group
    /// re-wraps the same pair, and <c>Prune</c> collapses the leftover single-child group, so the
    /// shape flips rather than deepening.
    /// </para>
    /// </remarks>
    private bool TryRegroupToFit(LayoutTree tree, Rect workArea, nint handle)
    {
        // Three or more, and the threshold is the whole justification. Standing one window beside
        // the others leaves THEIR arrangement untouched -- same axis, same order, same neighbours.
        // With only two there are no others: extracting one flips the entire layout, and which way
        // the user wants their two windows arranged is their decision, not a consequence of one of
        // them having a minimum size. A pair that does not fit is parked, exactly as before, and
        // the user can flip it themselves.
        if (!_registry.TryGetLeaf(handle, out var leaf) || leaf is null
            || leaf.Parent is not { Children.Count: >= 3 }
            || !tree.ExtractToOppositeAxis(leaf))
        {
            return false;
        }

        return !DoesNotFitItsFloor(tree, workArea, handle);
    }

    /// <summary>
    /// Whether a known floor is taller or wider than the tile <paramref name="handle"/> would be
    /// given by <paramref name="tree"/> on <paramref name="workArea"/>.
    /// </summary>
    /// <remarks>
    /// The leaf must already be in the tree -- the tile only exists once the window has a place in
    /// it -- but nothing is POSITIONED to work it out. A window with no floor on record answers no
    /// and costs nothing, which is every window the first time it is seen.
    /// </remarks>
    private bool DoesNotFitItsFloor(ITilingEngine tree, Rect workArea, nint handle)
    {
        if (!_minimumSize.TryGetValue(handle, out var floor)
            || !_registry.TryGetLeaf(handle, out var leaf) || leaf is null)
        {
            return false;
        }

        TreeArranger.Arrange(tree, workArea);

        var tile = TreeArranger.TileOf(leaf);
        return tile.Width > 0 && tile.Height > 0
            && (tile.Width < floor.Width || tile.Height < floor.Height);
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
    /// <summary>What one window failing to arrive on its tile turned out to MEAN.</summary>
    private enum ArrivalVerdict
    {
        /// <summary>On its tile, or not yet enough evidence to say anything.</summary>
        Inconclusive,

        /// <summary>Obeys its corner and OVERFLOWS its tile: it will not go under a size. Not a fault, but it covers its neighbour.</summary>
        MinimumSize,

        /// <summary>Obeys its corner and cannot FILL its tile: it will not go over a size. Not a fault and harms nobody.</summary>
        Underfills,

        /// <summary>Missed the same tile <see cref="MissesBeforeGivingUp"/> times. A fighter.</summary>
        Fighting,
    }

    /// <summary>
    /// Reads one failed arrival. See <see cref="ArrivalVerdict"/> for the three answers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two different failures used to come out of here as one. A FIGHTER accepts the reposition,
    /// reports success and turns up somewhere unrelated -- Clipchamp's input plumbing, placed at
    /// x=1724, read back x=4307 off the right edge of the desktop, forever. A window with a
    /// MINIMUM SIZE does the opposite: it honours the position exactly and every dimension it can
    /// meet, and raises only the one it cannot. Measured with NVIDIA Broadcast, offered
    /// [8,700 850x684] and landing on [8,700 850x772] twelve times running -- same corner, same
    /// width, and a height it will not go under.
    /// </para>
    /// <para>
    /// So the discriminator is the SHAPE of the miss, not how often it repeats. Repeating is what
    /// the two have in common: the fighter's landing is just as reproducible, which is why counting
    /// alone could never separate them and why twelve identical rounds were spent on a window that
    /// had answered the question on the second.
    /// </para>
    /// <para>
    /// The corner is the WHOLE discriminator, and an earlier reading of this that also demanded the
    /// window come back no smaller than its tile was wrong. Broadcast clamps its height UP to 772 in
    /// a short tile and DOWN to 1000 in a tall one -- one window, one range, both directions -- and
    /// the narrower rule read the second half as a fight and gave up on it permanently, which is the
    /// exact outcome this whole judgement exists to prevent. Clamping a SIZE is ordinary, documented
    /// Win32 behaviour; moving away from a POSITION it was given is not, and that is what a fighter
    /// does.
    /// </para>
    /// <para>
    /// Still required twice running. A window is at its own tile's corner at other moments too --
    /// mid-startup, mid-restore -- and one frame of that is not a constraint. Twice costs a single
    /// reconciliation tick and makes the coincidence unreachable.
    /// </para>
    /// </remarks>
    private ArrivalVerdict Judge(nint handle, Rectangle arrivedAt)
    {
        if (!_registry.TryGetLeaf(handle, out var leaf) || leaf is null)
        {
            return ArrivalVerdict.Inconclusive;
        }

        var tile = TreeArranger.TileOf(leaf);
        if (tile.Width <= 0 || tile.Height <= 0)
        {
            return ArrivalVerdict.Inconclusive;
        }

        if (arrivedAt.Left == tile.X && arrivedAt.Top == tile.Y
            && arrivedAt.Width == tile.Width && arrivedAt.Height == tile.Height)
        {
            // Landed. Whatever it did before this is forgiven -- an application that wobbles while
            // it starts up and then behaves is not a fighter.
            _misses.Remove(handle);
            return ArrivalVerdict.Inconclusive;
        }

        var repeated = _misses.TryGetValue(handle, out var seen) && seen.Tile.Equals(tile);

        if (repeated && seen.LandedAt == arrivedAt && ObeysItsCorner(tile, arrivedAt))
        {
            _misses.Remove(handle);
            return Overflows(tile, arrivedAt) ? ArrivalVerdict.MinimumSize : ArrivalVerdict.Underfills;
        }

        var misses = repeated ? seen.Misses + 1 : 1;
        _misses[handle] = (tile, arrivedAt, misses);
        return misses >= MissesBeforeGivingUp ? ArrivalVerdict.Fighting : ArrivalVerdict.Inconclusive;
    }

    /// <summary>
    /// The floor to record from one failed arrival, taken PER AXIS.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A floor is what the window REFUSED to go under, not everything it happened to measure while
    /// refusing. Measured with NVIDIA Broadcast: offered a wide short row of 3424x454 it landed on
    /// 1945x772 -- it raised the height it would not shrink to, and on the other axis took barely
    /// half the width it was offered. Recording the whole rectangle wrote 1945 down as a minimum
    /// WIDTH, and the same window had been tiled happily in a 1136-wide tile minutes earlier. It
    /// was then locked out of every slot it would have accepted.
    /// </para>
    /// <para>
    /// The two are distinguishable from the one rectangle. A dimension is a floor only where the
    /// window came back BIGGER than the tile; where it came back smaller it obeyed, and a window
    /// that underfills an axis has said nothing at all about a minimum on it.
    /// </para>
    /// <para>
    /// The axis that says nothing keeps whatever was already known rather than being cleared, so a
    /// floor demonstrated in one arrangement is not forgotten by a later arrival that happens not
    /// to test it. Zero means no floor known on that axis, which every comparison reads as "fits".
    /// </para>
    /// </remarks>
    private (int Width, int Height, IDisplay LastSeenOn) FloorFrom(nint handle, Rectangle landedAt, IDisplay display)
    {
        _minimumSize.TryGetValue(handle, out var known);

        if (!_registry.TryGetLeaf(handle, out var leaf) || leaf is null)
        {
            return (landedAt.Width, landedAt.Height, display);
        }

        var tile = TreeArranger.TileOf(leaf);
        return (
            landedAt.Width > tile.Width ? landedAt.Width : known.Width,
            landedAt.Height > tile.Height ? landedAt.Height : known.Height,
            display);
    }

    /// <summary>
    /// Renders a floor for the trace, saying only what is actually known. An axis with no
    /// demonstrated minimum is left out rather than printed as a zero nobody can read.
    /// </summary>
    private static string Describe((int Width, int Height, IDisplay LastSeenOn) floor) => (floor.Width, floor.Height) switch
    {
        ( > 0, > 0) => $"{floor.Width}x{floor.Height}",
        ( > 0, _) => $"{floor.Width} wide",
        (_, > 0) => $"{floor.Height} tall",
        _ => "any size",
    };

    /// <summary>
    /// Whether <paramref name="handle"/> is sitting at its tile's corner and wholly within it --
    /// where a window that merely cannot FILL its slot belongs, and where there is nothing for a
    /// reflow to do.
    /// </summary>
    private bool SitsInsideItsTile(nint handle, Rectangle at)
    {
        if (!_registry.TryGetLeaf(handle, out var leaf) || leaf is null)
        {
            return false;
        }

        var tile = TreeArranger.TileOf(leaf);
        return tile.Width > 0 && tile.Height > 0 && ObeysItsCorner(tile, at) && !Overflows(tile, at);
    }

    /// <summary>
    /// Whether the window went exactly where it was put. This is what a fighter gets wrong and a
    /// constrained window gets right, and it is the only thing that separates them.
    /// </summary>
    private static bool ObeysItsCorner(Rect tile, Rectangle arrivedAt) =>
        arrivedAt.Left == tile.X && arrivedAt.Top == tile.Y;

    /// <summary>
    /// Whether the window spills OUT of the tile it obeyed the corner of, rather than merely failing
    /// to fill it. The two are the same kind of fact about the window and want opposite answers: one
    /// is drawn over the neighbour's tile, the other leaves a gap inside its own.
    /// </summary>
    private static bool Overflows(Rect tile, Rectangle arrivedAt) =>
        arrivedAt.Width > tile.Width || arrivedAt.Height > tile.Height;

    /// <summary>
    /// Takes a window out of the tree and hands its space back to the survivors, leaving it
    /// floating where it is. The shared half of giving up and of parking a window that cannot fit.
    /// </summary>
    private void Untile(nint handle, LayoutTree tree, IDisplay display)
    {
        _misses.Remove(handle);
        _owners.Remove(handle);

        if (WorkspaceSessionAdapter.RemoveWindow(tree, _registry, handle))
        {
            TreeArranger.ArrangeAndPosition(tree, _registry, WorkAreaResolver.Resolve(display), _afterArrange);
        }
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
        _minimumSize.Remove(handle);
        _clampsInsideItsTile.Remove(handle);

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

        RetryParkedOn(display);
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
        if (!e.IsUserGesture)
        {
            // A window that has already demonstrated it clamps inside its tile is not a new
            // question every two seconds. Measured with the real NVIDIA Broadcast: judged
            // `Underfills`, kept its tile -- correctly -- and then reflowed on every single poll
            // for the life of the window.
            //
            // The same self-sustaining shape as the parked-window storm, and the same fuel. The
            // reflow re-asks the window for the height it has just refused, the ask is filed as its
            // bounds, and the next reconciliation pass reads the gap between that ask and the
            // window's real size as a fresh bounds change. Nothing about the tree ever changed.
            //
            // Only while it is still INSIDE the tile it holds. A tile that shrank under the window
            // is a different fact -- possibly a floor, which must untile it -- so that goes back to
            // being judged.
            if (_clampsInsideItsTile.Contains(handle) && SitsInsideItsTile(handle, before))
            {
                return;
            }

            switch (Judge(handle, before))
            {
                case ArrivalVerdict.Fighting:
                    _givenUp.Add(handle);

                    Trace?.Record(
                        $"gave up hwnd=0x{handle:X} class={window.ClassName} proc={window.ProcessName} " +
                        $"-- never reached its tile in {MissesBeforeGivingUp} attempts; evicted and refused");

                    Untile(handle, tree, display);
                    return;

                // Not a fault and not refused. The window answered a question about ITSELF, so the
                // answer is kept and the window is left untiled only for as long as it is true --
                // close a neighbour and the slot that fits it exists, and it tiles like anything
                // else. `_givenUp` is deliberately NOT touched: that is a sentence for the life of
                // the handle, and this window has done nothing wrong.
                case ArrivalVerdict.MinimumSize:
                    var floor = FloorFrom(handle, before, display);
                    _minimumSize[handle] = floor;

                    // Reshaping the tree comes BEFORE giving up on it. A window asking for more
                    // than its share is a fact about the arrangement, not about the window, and the
                    // arrangement is the thing this owns.
                    if (TryRegroupToFit(tree, WorkAreaResolver.Resolve(display), handle))
                    {
                        Trace?.Record(
                            $"regrouped hwnd=0x{handle:X} class={window.ClassName} proc={window.ProcessName} " +
                            $"-- given a branch of its own so {Describe(floor)} fits");

                        // Out of the switch rather than out of the method: the reflow at the bottom
                        // is what puts every window on the new shape.
                        break;
                    }

                    Trace?.Record(
                        $"minimum size hwnd=0x{handle:X} class={window.ClassName} proc={window.ProcessName} " +
                        $"-- will not go under {Describe(floor)}; untiled until a tile that fits exists");

                    Untile(handle, tree, display);
                    return;

                // Keeps its tile. The window is inside the slot it was given, just not filling it,
                // so the only thing it costs anybody is a little empty space of its own -- and
                // evicting a window over that would spend a tiled window to tidy away a gap.
                // Reported once rather than every pass: the fact does not change, and a line every
                // two seconds for the rest of the session is how the trace stopped being readable
                // the last time something benign was logged on a tick.
                case ArrivalVerdict.Underfills:
                    if (_clampsInsideItsTile.Add(handle))
                    {
                        Trace?.Record(
                            $"clamps itself hwnd=0x{handle:X} class={window.ClassName} proc={window.ProcessName} " +
                            $"-- will not go over {before.Width}x{before.Height}; kept, and its tile is not filled");
                    }

                    break;
            }
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

        // A tiled window moving is this adapter's only evidence that the SHAPE of the display
        // changed, and a reshape is the other moment a slot can grow.
        //
        // Reported from real use: Alt+O stacks the group, NVIDIA Broadcast's tile becomes 244
        // pixels tall against a floor of 772 and it is untiled -- correct. Alt+O again puts the
        // columns back, 1383 tall, and nothing asked. The retry was wired to a window LEAVING, on
        // the reasoning that admitting one only ever divides the space further; true, and beside
        // the point, because an orientation toggle, a resize chord and a drag all reshape slots
        // without anything leaving. The user was left with a floating window and no way back except
        // closing something.
        //
        // Affordable because admission with a floor already on record no longer costs a reflow: it
        // arranges to measure and moves nothing. On the overwhelming majority of passes there is
        // nothing parked and this is a dictionary scan over an empty set.
        //
        // Terminates without a guard. RetryParkedOn announces an arrival, and admission either
        // tiles the window or leaves it untiled without touching it -- neither path re-enters this
        // one, and `_arriving` already refuses a re-entrant announcement.
        RetryParkedOn(display);
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
