using CosmicWin.App.Diagnostics;
using CosmicWin.App.Input;
using CosmicWin.Interop;
using CosmicWin.Layout;

namespace CosmicWin.App;

/// <summary>
/// Task 2.14: the App-layer <see cref="IActionScheduler"/> that turns a dispatched <see
/// cref="HotkeyAction"/> into <see cref="ITilingEngine"/> tree mutations, then arranges and
/// positions the affected windows via <see cref="WindowRegistry"/>. Owns no lifetime over
/// <paramref name="engine"/>/<paramref name="registry"/>/<paramref name="foreground"/> — all
/// three are supplied and disposed by the composition root (no ownership leakage); this class
/// only reads from <see cref="WindowRegistry"/>, never registers or removes entries.
/// </summary>
public sealed class ActionExecutor(
    ITilingEngine engine,
    WindowRegistry registry,
    IForegroundWindowSource foreground) : IActionScheduler
{
    private LeafNode? _focused;

    /// <summary>
    /// HA-1's <c>Alt+[</c>/<c>Alt+]</c>: the node the next Move/Toggle/Resize acts on. Null means
    /// the focused leaf itself, which is the default and reproduces the pre-scope behaviour exactly.
    /// LE-3, LE-5 and LE-6 are all written in terms of the focused NODE, and <see
    /// cref="ITilingEngine"/> already accepts <see cref="Node"/>, so ascending does not add an
    /// operation -- it selects which node the existing ones receive.
    /// </summary>
    private Node? _focusScope;

    /// <summary>
    /// Which window held focus on each desktop the moment the user walked away from it, so walking
    /// back puts them where they were instead of on whatever tile the tree happens to list first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Keyed by (display, desktop) -- the SAME pair <see cref="TreeManager"/> keys its layout trees
    /// by. "Which window was I on" is a question per screen: two monitors are two places the user
    /// was, and one slot for the machine would have the second monitor answering for the first.
    /// </para>
    /// <para>
    /// Only desktops CosmicWin saw the user leave are in here. Task View and Win+Ctrl+arrow are
    /// departures nothing observes, so they leave no record and the arrival falls back to the first
    /// tile exactly as it always did -- a gap in the improvement, never a new failure.
    /// </para>
    /// <para>
    /// Nothing invalidates it. A recalled handle is checked against the tree the user is looking at
    /// before it is used, so a window that died or was rehomed drops out on its own; an entry that
    /// outlives its window is inert rather than wrong, which is what makes an invalidation hook
    /// nobody remembers to wire unnecessary here.
    /// </para>
    /// </remarks>
    private readonly Dictionary<(nint Display, Guid Desktop), nint> _focusByDesktop = [];

    /// <summary>
    /// Windows' virtual desktops, or <see langword="null"/> when the composition did not wire them
    /// (every unit test that predates the feature). Settable rather than a constructor parameter for
    /// the same reason <see cref="TreeManager"/> and <see cref="FocusTrace"/> are.
    /// </summary>
    public CosmicWin.Interop.IVirtualDesktopService? VirtualDesktops { get; set; }

    /// <summary>Where desktop chords report what they actually did. Null disables the trace.</summary>
    public Diagnostics.IDesktopTrace? DesktopTrace { get; set; }

    /// <summary>
    /// Called after a window has actually landed on another desktop, so the layouts can catch up
    /// with the shell. Moving a window changes only where Windows draws it; the tree it left and
    /// the tree it joined both have to hear about it.
    /// </summary>
    public Action<nint>? WindowMovedToDesktop { get; set; }

    /// <summary>
    /// Called the instant a desktop switch lands, so the arriving layout is applied before the user
    /// sees it. Left to the reconciliation timer instead, it arrives up to a full interval late --
    /// measured as windows appearing loose and then snapping into place.
    /// </summary>
    public Action? DesktopSwitched { get; set; }

    /// <summary>The monitor work area <see cref="ITilingEngine.Arrange"/> lays leaves out into.</summary>
    public Rect WorkArea { get; set; }

    /// <summary>
    /// When set, every mutation resolves and arranges
    /// the FOCUSED window's OWN monitor tree/work area, instead of always the primary <paramref
    /// name="engine"/>/<see cref="WorkArea"/> — restoring tree/screen agreement on secondary
    /// monitors. Null preserves the pre-, primary-only behavior, so tests that construct this
    /// class directly without a monitor topology stay unaffected.
    /// </summary>
    public TreeManager? TreeManager { get; set; }

    /// <summary>
    /// MR-2 diagnosis: when set, every FOCUS chord records what actually
    /// happened along the focus path -- the leaf it started from, the tree-walk result, the target
    /// handle and whether activation succeeded. Focus chords are the only ones traced; Move/Resize
    /// are known to work and stay silent. Null keeps the executor entirely untraced, so every test
    /// and call site that does not care is unaffected.
    /// </summary>
    public IFocusTrace? FocusTrace { get; set; }

    /// <summary>
    /// The leaf CosmicWin currently treats as focused, resolved the same way a chord resolves it:
    /// the OS foreground when it maps to a tracked window, otherwise the last leaf successfully
    /// activated. LE-4's window placement asks for this when a window arrives -- and by then the
    /// newcomer has usually already stolen the foreground, so the fallback is what answers, naming
    /// the tile the user was actually on.
    /// </summary>
    public LeafNode? ResolveFocusedLeaf() =>
        TryResolveFocused(foreground.GetForegroundHandle(), out var leaf) ? leaf : null;

    public ValueTask ScheduleAsync(HotkeyAction action, CancellationToken cancellationToken)
    {
        try
        {
            Execute(action);
        }
        finally
        {
            // In a finally so a throw on the tiling path cannot leave the border stranded on a
            // rectangle that no longer exists.
            AfterAction?.Invoke();
        }

        return ValueTask.CompletedTask;
    }

    private void Execute(HotkeyAction action)
    {
        var foregroundHandle = foreground.GetForegroundHandle();

        // Desktop chords are answered BEFORE focus is resolved. They are about which desktop the
        // user is looking at, not about the tiling tree, and they must keep working when the
        // foreground window is one CosmicWin does not track at all -- a dialog, an excluded app, or
        // a desktop that happens to be empty.
        if (TryDispatchDesktop(action, foregroundHandle))
        {
            return;
        }

        // Same reasoning, one chord later: this is about the window the user is looking at, not
        // about the tree. Letting it fall through to the tiling path would aim it at the last
        // tracked leaf instead -- closing a window the user is not even looking at.
        if (TryDispatchClose(action, foregroundHandle))
        {
            return;
        }

        // A chord that MOVES a window acts on the window the user is looking at, or on nothing in the
        // tree at all. TryResolveFocused deliberately falls back to the last known leaf when the
        // foreground is untracked, and that is right for a FOCUS chord -- it is how a user returns to
        // the tiled world from a dialog or a non-tiled app, where dropping the chord would strand
        // them. For a mutation it is wrong twice over. Reported from real use: with a modal dialog
        // focused, Alt+Shift+<direction> rearranged the window BEHIND it. The dialog is owned, so it
        // is never in the tree (measured), and the chord landed on whatever had been focused before.
        //
        // Answered BEFORE focus is resolved, exactly like the desktop chords above: this is about a
        // window the tree does not contain, so making it wait on a resolved leaf would drop the chord
        // whenever the tree is empty or nothing has been focused yet.
        if (IsMutation(action.Kind) && !IsTracked(foregroundHandle))
        {
            // Doing nothing was only half the answer. A floating window still deserves the chord --
            // it simply cannot travel through a tree it is not in, so the direction goes to whoever
            // manages it. Resize and toggle-axis are deliberately not offered: half a work area is a
            // position rather than a size a dialog laid itself out for, and a window in no group has
            // no split axis to toggle.
            if (MoveDirectionOf(action.Kind) is { } floating)
            {
                MoveFloatingWindow?.Invoke(foregroundHandle, floating);
            }

            return;
        }

        if (TryResolveFocused(foregroundHandle, out var focused))
        {
            Dispatch(action.Kind, focused, foregroundHandle);
        }
        else if (FocusDirectionOf(action.Kind) is { } direction && !TryEnterTheTree(direction, foregroundHandle))
        {
            // The chord never reached the tree walk: recorded rather than dropped, so a silent
            // focus chord on real hardware can be told apart from a failed one -- and the
            // foreground handle names the untracked window that was holding focus instead.
            Trace(direction, foregroundHandle, 0, 0, FocusTraceOutcome.UnresolvedFocus);
        }

        return;
    }

    /// <summary>
    /// Handles a move chord for a window the tree does not contain, reporting whether anyone owned
    /// it. Unset -- as in every test that predates floating dialogs -- such a chord is simply dropped.
    /// </summary>
    public Func<nint, Direction, bool>? MoveFloatingWindow { get; set; }

    /// <summary>
    /// Where a window actually IS, including one the tree does not hold. Unset -- as in every test
    /// that predates it -- a focus chord from outside the tree stays the no-op it always was.
    /// </summary>
    /// <remarks>
    /// The registry holds only tiled leaves, so it cannot answer for the very window this is asked
    /// about. The WORKSPACE tracks every top-level window, which is where production resolves it --
    /// the same split, and the same reason, as <see cref="ActivateUntrackedWindow"/>.
    /// </remarks>
    public Func<nint, Interop.Rectangle?>? ResolveWindowBounds { get; set; }

    /// <summary>
    /// Puts focus back on a window the TREE does not hold, reporting whether it took. Unset -- as
    /// in every test that predates it -- nothing untracked is ever restored.
    /// </summary>
    /// <remarks>
    /// <see cref="RestoreFocusTo"/> reaches a window through the registry, which holds only tiled
    /// leaves. Sending an UNTRACKED window to another desktop is a legitimate ask and the chord
    /// path says so explicitly, so the hand-off fires for it too -- and then had nothing to undo
    /// with when the shell refused, silently leaving the user on a tile they never chose. The
    /// window is still known to the WORKSPACE, which tracks every top-level window rather than
    /// only the tiled ones, so production resolves it from there.
    /// </remarks>
    public Func<nint, bool>? ActivateUntrackedWindow { get; set; }

    /// <summary>
    /// Asks the window at this handle to close, reporting whether the ask was DELIVERED. Unset --
    /// as in every test that predates it -- no chord ever closes anything.
    /// </summary>
    /// <remarks>
    /// A delegate rather than a registry lookup for the same reason the desktop chords read the OS
    /// foreground: closing a window CosmicWin does not tile is an ordinary thing to want, and the
    /// registry holds tiled leaves only. Production resolves the registry first and the workspace
    /// second.
    /// </remarks>
    public Func<nint, bool>? CloseWindowAt { get; set; }

    /// <summary>
    /// Invoked after every chord this executor answers, whatever it did.
    /// </summary>
    /// <remarks>
    /// Exists for anything drawn ON TOP of the layout rather than by it. The focus border was
    /// following the 400ms reconciliation tick, so a chord that re-laid the tree left it on the old
    /// rectangle for up to half a second -- most visible on Alt+O, where every window moves at once.
    /// The tick stays as the safety net for changes no chord caused, such as a mouse click landing
    /// on another window.
    /// </remarks>
    public Action? AfterAction { get; set; }

    /// <summary>The direction a MOVE chord names, or <see langword="null"/> if it is not a move.</summary>
    private static Direction? MoveDirectionOf(HotkeyActionKind kind) => kind switch
    {
        HotkeyActionKind.MoveLeft => Direction.Left,
        HotkeyActionKind.MoveRight => Direction.Right,
        HotkeyActionKind.MoveUp => Direction.Up,
        HotkeyActionKind.MoveDown => Direction.Down,
        _ => null,
    };

    /// <summary>
    /// Whether this chord CHANGES the layout, as opposed to navigating it.
    /// </summary>
    /// <remarks>
    /// Scope changes (<see cref="HotkeyActionKind.FocusIn"/>/<see cref="HotkeyActionKind.FocusOut"/>)
    /// are navigation, not mutation: they choose what a later chord will act on and move nothing by
    /// themselves.
    /// </remarks>
    private static bool IsMutation(HotkeyActionKind kind) => kind
        is HotkeyActionKind.MoveLeft or HotkeyActionKind.MoveRight
        or HotkeyActionKind.MoveUp or HotkeyActionKind.MoveDown
        or HotkeyActionKind.ResizeLeft or HotkeyActionKind.ResizeRight
        or HotkeyActionKind.ResizeUp or HotkeyActionKind.ResizeDown
        or HotkeyActionKind.ToggleOrientation;

    /// <summary>Whether the OS foreground maps to a leaf CosmicWin actually holds.</summary>
    private bool IsTracked(nint foregroundHandle) =>
        foregroundHandle != 0 && registry.TryGetLeaf(foregroundHandle, out var leaf) && leaf is not null;

    private void Dispatch(HotkeyActionKind kind, LeafNode focused, nint foregroundHandle)
    {
        switch (kind)
        {
            case HotkeyActionKind.FocusLeft: MoveFocus(Direction.Left, focused, foregroundHandle); break;
            case HotkeyActionKind.FocusRight: MoveFocus(Direction.Right, focused, foregroundHandle); break;
            case HotkeyActionKind.FocusUp: MoveFocus(Direction.Up, focused, foregroundHandle); break;
            case HotkeyActionKind.FocusDown: MoveFocus(Direction.Down, focused, foregroundHandle); break;
            case HotkeyActionKind.MoveLeft: MutateScope(focused, (e, n) => e.MoveNode(Direction.Left, n)); break;
            case HotkeyActionKind.MoveRight: MutateScope(focused, (e, n) => e.MoveNode(Direction.Right, n)); break;
            case HotkeyActionKind.MoveUp: MutateScope(focused, (e, n) => e.MoveNode(Direction.Up, n)); break;
            case HotkeyActionKind.MoveDown: MutateScope(focused, (e, n) => e.MoveNode(Direction.Down, n)); break;
            case HotkeyActionKind.ToggleOrientation: MutateScope(focused, (e, n) => e.ToggleAxis(n)); break;
            case HotkeyActionKind.ResizeLeft: MutateScope(focused, (e, n) => e.ResizeNode(Direction.Left, n)); break;
            case HotkeyActionKind.ResizeRight: MutateScope(focused, (e, n) => e.ResizeNode(Direction.Right, n)); break;
            case HotkeyActionKind.ResizeUp: MutateScope(focused, (e, n) => e.ResizeNode(Direction.Up, n)); break;
            case HotkeyActionKind.ResizeDown: MutateScope(focused, (e, n) => e.ResizeNode(Direction.Down, n)); break;
            case HotkeyActionKind.FocusOut: AscendScope(focused); break;
            case HotkeyActionKind.FocusIn: DescendScope(focused); break;
        }
    }

    /// <summary>
    /// Gives focus to a window on the desktop the user is looking at NOW, after the set of windows
    /// on screen changed desktop. Called for both halves of that: the focused window sent away, and
    /// the user walking to another desktop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reported from real use, twice and separately: the window went away and CosmicWin went on
    /// treating it as focused; then the user switched desktops and focus stayed behind on the one
    /// they left. One cause. <see cref="_focused"/>'s only liveness test is <c>IsAlive</c>, and a
    /// window on another virtual desktop is perfectly alive -- DWM CLOAKS it rather than destroying
    /// it, which this repository has already measured leaves <c>WS_VISIBLE</c> set. The cache
    /// therefore cannot tell "that desktop is not on screen any more" from "that window still
    /// exists", and kept answering with the one window that was certainly wrong.
    /// </para>
    /// <para>
    /// So the cache is dropped FIRST and unconditionally, before anything else can fail. Everything
    /// after it is an improvement on "no focus"; none of it is required for the cache to stop lying.
    /// </para>
    /// <para>
    /// But for a SWITCH, dropping it is not enough on its own, and that is worth stating plainly:
    /// the reconciliation tick calls <see cref="ResolveFocusedLeaf"/> every interval, and its OS
    /// foreground branch wins. The registry spans every desktop, so the cloaked window the user just
    /// left still resolves to a tracked leaf and would be written straight back into the cache
    /// within one tick. Activating a window on the ARRIVING desktop is what actually settles it,
    /// because the OS foreground is the authority and this is the only way to move it.
    /// </para>
    /// <para>
    /// <paramref name="departingHandle"/> is the window being sent away, or 0 when the whole desktop
    /// changed underneath and nothing in particular is leaving.
    /// </para>
    /// </remarks>
    /// <param name="arriving">
    /// Whether the user has WALKED to another desktop, as opposed to a window having been sent away
    /// while they stayed put. Passed explicitly rather than inferred from
    /// <paramref name="departingHandle"/> being zero: those two happen to coincide today, and the
    /// first version of the thread reading was silently blank on the arriving path for exactly that
    /// reason. Only an arrival revives anything -- on a send, the record for the desktop in view
    /// names the window being sent away at this very instant.
    /// </param>
    private void HandFocusToVisibleDesktop(nint departingHandle, bool arriving)
    {
        // Read from the OS, not from the cache. The cache is what is being distrusted here, and the
        // pair of readings around the activation is the only thing that separates "the foreground
        // really moved" from "we were told it did" -- Activate reports FIVE outcomes and
        // IWindow.TryActivate flattens them all to one bool, AlreadyForeground included.
        var foregroundBefore = foreground.GetForegroundHandle();

        var departed = _focused;
        _focused = null;

        // Dropped with it. A scope is an ascent from a leaf, and the leaf it ascended from has just
        // left the desktop -- keeping it would aim the next Move at a group nobody is inside.
        _focusScope = null;

        // No tree manager means no way to name the tree the user is looking at, so no survivor can
        // be chosen. Dropping the stale cache above was still right, and is still the half that
        // fixes the reported defect.
        if (TreeManager is not { } treeManager)
        {
            DesktopTrace?.Record(
                $"handover departing=0x{departingHandle:X} fg-before=0x{foregroundBefore:X} " +
                $"-- no tree manager");
            return;
        }

        // WHICH monitor to search. The departing window answers it when there is one -- the registry
        // spans every desktop, so it resolves even now, and its bounds are where it was last laid
        // out. On a switch nothing is departing, so the window the cache just named answers instead:
        // the user is still looking at the monitor they were on. Neither answering falls through to
        // ResolveDisplay's documented Primary fail-safe.
        var bounds = registry.TryGetWindow(departingHandle, out var departing) && departing is not null
            ? departing.Bounds
            : departed is not null
              && registry.TryGetWindow(departed.Window.Handle, out var cached) && cached is not null
                ? cached.Bounds
                : Interop.Rectangle.Empty;

        var display = treeManager.ResolveDisplay(bounds);
        if (treeManager.FocusSurvivorOn(display, departingHandle) is not
            { Status: FocusWalkStatus.Found, Leaf: { } survivor })
        {
            // An empty desktop is a legitimate answer, not a failure. No window means no focus, and
            // inventing one would drag the user somewhere they never asked to go.
            DesktopTrace?.Record(
                $"handover departing=0x{departingHandle:X} fg-before=0x{foregroundBefore:X} " +
                $"display=0x{display.Handle:X} " +
                $"fg-before-thread-active=0x{foreground.GetActiveWindowOfThreadOwning(foregroundBefore):X} " +
                $"-- no survivor");
            return;
        }

        // ARRIVAL only, for the same reason the sweep below is: on a send the user has not gone
        // anywhere, so the record for the desktop in view names the window being sent away at this
        // very instant -- recalling it would hand focus straight back to what is leaving.
        var recalled = arriving ? RecallFocusOn(display) : null;
        var landing = recalled ?? survivor;

        // ARRIVAL only. On a send the user has not gone anywhere -- the window has -- so the desktop
        // in view is the one they were already on, and sweeping it would activate every tile on it
        // including, one moment before the shell takes it away, the window being sent.
        if (arriving)
        {
            // A RECALLED landing window is swept like every other tile -- landingOn: 0 excludes
            // nobody, since no window has handle zero. It is not an oversight that it loses the
            // exemption: the window that held focus when this desktop was left is precisely the one
            // whose stale active frame the sweep exists to clear, because Windows cloaked it without
            // ever delivering WM_NCACTIVATE(FALSE). Excluding it would strand exactly the border the
            // sweep was written for. It costs one extra activation and it is activated again below,
            // last, so focus still ends where the user left it.
            //
            // A FALLBACK landing window keeps the exemption. That one is a tile the user was never
            // on, so there is no stale frame to clear and the second activation would buy nothing.
            SweepDesktop(display, landingOn: recalled is null ? landing.Window.Handle : 0);
        }

        // The OUTCOME, not the bool. Null distinguishes the third case the bool could not express:
        // nobody was asked at all, because the landing window is untracked or already dead.
        // Reporting that as a refusal would blame Windows for a decision taken right here.
        ActivationOutcome? outcome =
            registry.TryGetWindow(landing.Window.Handle, out var window) && window is { IsAlive: true }
                ? window.Activate()
                : null;
        var activated = outcome?.Confirmed() ?? false;

        if (activated)
        {
            // The same rule MoveFocus follows, and for the same reason it was written: the cache
            // advances only on a REAL activation, so a refused one cannot leave CosmicWin claiming
            // the user is somewhere they are not.
            _focused = landing;
        }

        // Recorded on EVERY outcome, not only the interesting one. The whole reason this line
        // exists is that a handover which quietly did nothing is indistinguishable, from outside,
        // from one that worked -- and a reported defect was already diagnosed twice from guesswork
        // because of it. This is the repository's own standing rule, written on FileDesktopTrace:
        // instrument before guessing, because a window manager's failures are invisible by nature.
        // fg-after is the whole point of this line. `activated` is the derived bool, and a bool
        // cannot tell a real foreground change from AlreadyForeground -- the exact short-circuit
        // MR-2 was caught by, which reported success while nothing moved on screen. Reading the OS
        // back says which of the two happened, and it is the only reading that can.
        // `activation` names the RUNG, which is the other half: a plain SetForegroundWindow and an
        // AttachThreadInput-backed retry both spell success, and only the second one touches
        // another process's input state.
        // The two thread readings are taken AFTER the handover on purpose. What the system believes
        // and what a given UI thread believes are separate pieces of state, and the case worth
        // catching is them DISAGREEING -- the departing thread still naming itself while fg-after
        // names the survivor. Neither reading proves anything alone, which is why they share a line.
        DesktopTrace?.Record(
            $"handover departing=0x{departingHandle:X} fg-before=0x{foregroundBefore:X} " +
            $"display=0x{display.Handle:X} survivor=0x{survivor.Window.Handle:X} " +
            // Both, not one derived from the other. `recalled` says whether the memory answered at
            // all -- zero on a first visit, on a send, and on a record the tree no longer backs --
            // and `landing` says where focus actually went. A single field could not tell a recall
            // that was refused apart from one that was never consulted.
            $"recalled=0x{recalled?.Window.Handle ?? 0:X} landing=0x{landing.Window.Handle:X} " +
            $"activation={outcome?.ToString() ?? "not-asked"} activated={activated} " +
            $"fg-after=0x{foreground.GetForegroundHandle():X} " +
            // Keyed to fg-before, NOT to departingHandle. The arriving path calls in with a
            // departing handle of zero -- nothing is being sent anywhere -- so a reading keyed to it
            // asked the OS about window zero and recorded nothing, on exactly the path the defect
            // reproduces on. fg-before names the real window on both paths.
            $"fg-before-thread-active=0x{foreground.GetActiveWindowOfThreadOwning(foregroundBefore):X} " +
            $"survivor-thread-active=0x{foreground.GetActiveWindowOfThreadOwning(survivor.Window.Handle):X}");
    }

    /// <summary>
    /// Activates every window on <paramref name="display"/> except <paramref name="landingOn"/>, so
    /// each one is focused and then LEFT — the measured recipe for making a window that paints its
    /// own frame notice it is not active any more.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The defect it cures, measured rather than argued: switching away from a desktop never
    /// deactivates the window that held focus there. Windows CLOAKS it and delivers no
    /// <c>WM_NCACTIVATE(FALSE)</c>, so an application that paints its own non-client frame from
    /// thread-local activation state keeps drawing itself active and comes back wearing a focus
    /// border that belongs to nobody. The discriminator: leave the desktop with that window focused
    /// and its border strands; leave it with a DIFFERENT window focused and it comes back clean.
    /// Seven other suspects were refuted before this one, several of them on hardware.
    /// </para>
    /// <para>
    /// Swept rather than aimed at the single window that needs it, and that is a deliberate reversal.
    /// The aimed version has to REMEMBER which window held focus on each desktop, and the moment
    /// between a switch landing and focus being handed on -- where the current desktop id and the OS
    /// foreground disagree about which desktop anything belongs to -- made that record wrong: every
    /// handover line reported the remembered window as living elsewhere, and it never fired once.
    /// The sweep remembers nothing, so it has nothing to key wrong.
    /// </para>
    /// <para>
    /// What it COSTS was written here as "one or two activations reporting <c>Direct</c> or
    /// <c>AttachedInput</c>, never the synthetic-Alt rung". The first trace of a real session
    /// refuted every part of that: across roughly thirty swept activations, five were free
    /// (<c>Direct</c>/<c>AlreadyForeground</c>), about twenty took the input-attach rung, and
    /// EIGHT reported <c>InputUnlocked</c> -- the synthetic-Alt rung, real <c>VK_MENU</c> traffic
    /// on the user's desktop, which the interop's own doc warns can trip menu accelerators. One
    /// sweep visited three windows, not "one or two".
    /// </para>
    /// <para>
    /// The expensive rung is load-bearing and is NOT capped: a window has to GAIN the foreground
    /// before it can lose it, and losing it is the whole repaint this exists to cause. Refusing to
    /// escalate would leave roughly a quarter of them still painted active, which is the defect.
    /// What is capped is the total: see <see cref="SweepBudget"/>.
    /// </para>
    /// <para>
    /// <paramref name="landingOn"/> is skipped rather than visited early, because the caller
    /// activates it immediately afterwards. Visiting it here would activate it twice and, worse,
    /// would leave it as a window the sweep departed from — the exact state this is trying to
    /// clear.
    /// </para>
    /// <para>
    /// A refusal never stops the walk. Windows says no to activation for ordinary reasons, and
    /// abandoning the sweep at the first one would strand the user's focus on whichever window it
    /// reached — a worse outcome than the border it set out to clear.
    /// </para>
    /// </remarks>
    /// <summary>
    /// How long the whole arrival sweep may spend STARTING activations, however many tiles it has.
    /// </summary>
    /// <remarks>
    /// Per-sweep rather than per-window, which is the bound that was missing. Each activation is
    /// already bounded on its own (the interop's 250 ms), but N of them in a row are not, and this
    /// walk runs on the WPF UI thread -- the reconciliation tick is a <c>DispatcherTimer</c>, chosen
    /// so it serialises with the focus border rather than racing it. A desktop of tiles whose
    /// activations all time out would freeze every one of those for the sum.
    /// <para>
    /// Never observed: the first real trace recorded zero timeouts. This bounds the tail, it does
    /// not fix a measured freeze -- and the honest bound is the budget PLUS one activation's own,
    /// since the check is what gates STARTING the next one, not interrupting the one in flight.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan SweepBudget = TimeSpan.FromMilliseconds(250);

    /// <summary>Reads the current time for <see cref="SweepBudget"/>. Replaceable so the bound is testable.</summary>
    /// <remarks>
    /// A bound that only a slow machine can exercise is a bound nobody has tested -- the same
    /// reasoning that made the interop take its attempt as a delegate.
    /// </remarks>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    private void SweepDesktop(IDisplay display, nint landingOn)
    {
        if (TreeManager is not { } treeManager)
        {
            return;
        }

        var visited = new List<string>();
        var startedAt = Clock();
        var abandoned = 0;
        foreach (var leaf in treeManager.LeavesOn(display))
        {
            var handle = leaf.Window.Handle;
            if (handle == landingOn)
            {
                continue;
            }

            // Checked before STARTING one, never to interrupt one in flight: a half-done activation
            // is the state this walk exists to clear, so abandoning inside one would manufacture
            // the defect. Stopping between them costs the windows not reached their repaint -- they
            // keep the stale border they already had -- which is strictly what we came to fix
            // failing to help, not a new harm. Focus is unaffected either way: the caller activates
            // the landing window after this returns, swept or not.
            if (Clock() - startedAt >= SweepBudget)
            {
                abandoned++;
                continue;
            }

            if (!registry.TryGetWindow(handle, out var window) || window is not { IsAlive: true })
            {
                visited.Add($"0x{handle:X}:untracked");
                continue;
            }

            visited.Add($"0x{handle:X}:{window.Activate()}");
        }

        // Recorded even when it swept nothing. A sweep that visited an empty set and a sweep that
        // never ran look identical from a log that only speaks up when it has something to say.
        DesktopTrace?.Record(
            $"sweep display=0x{display.Handle:X} landing=0x{landingOn:X} " +
            $"count={visited.Count} {string.Join(" ", visited)}" +
            (abandoned > 0 ? $" budget-exhausted={abandoned}" : string.Empty));
    }

    /// <summary>
    /// Files the window the user is leaving <paramref name="desktop"/> on, so walking back later
    /// can put them on it again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called with the desktop read BEFORE the switch, because by the time focus is handed over the
    /// service already names the arriving one and the record would be filed under the wrong desktop.
    /// </para>
    /// <para>
    /// <see cref="Guid.Empty"/> is refused rather than used as a key. It is what the shell answers
    /// when it will not say which desktop this is, and every unknown desktop shares it -- so filing
    /// under it would let one desktop hand its window to another. Nothing recorded means the arrival
    /// falls back to the first tile, which is the honest answer to a question the shell declined.
    /// </para>
    /// <para>
    /// Only TRACKED windows are remembered. The arrival can activate nothing else, so a record for a
    /// dialog or an excluded app would be a slot that can only ever be refused -- and it would
    /// displace the real tile the user was on before that dialog opened.
    /// </para>
    /// </remarks>
    private void RememberFocusOn(Guid desktop, nint foregroundHandle)
    {
        if (TreeManager is not { } treeManager || desktop == Guid.Empty)
        {
            return;
        }

        // The foreground first, the cache second -- the same order every chord resolves focus in.
        // The cache answers when the user is looking at something CosmicWin does not track, which is
        // exactly when it is the better witness of which TILE they were working on.
        var leaf = foregroundHandle != 0
            && registry.TryGetLeaf(foregroundHandle, out var tracked) && tracked is not null
                ? tracked
                : _focused;

        if (leaf is null
            || !registry.TryGetWindow(leaf.Window.Handle, out var window) || window is not { IsAlive: true })
        {
            return;
        }

        _focusByDesktop[(treeManager.ResolveDisplay(window.Bounds).Handle, desktop)] = leaf.Window.Handle;
    }

    /// <summary>
    /// The window the user was on when they last left the desktop now in view on
    /// <paramref name="display"/>, or <see langword="null"/> when there is nothing to go back to.
    /// </summary>
    /// <remarks>
    /// The record is checked against <see cref="TreeManager.LeavesOn"/>, which reads the tree for
    /// the desktop CURRENTLY in view. That one lookup answers three questions at once: is the window
    /// still tiled, is it still on THIS desktop, and is it still on this monitor. A window that
    /// closed, was rehomed by a chord, or was dragged to another screen fails it and the arrival
    /// falls back -- which is why this needs no invalidation wired anywhere else.
    /// </remarks>
    private LeafNode? RecallFocusOn(IDisplay display)
    {
        if (TreeManager is not { } treeManager
            || VirtualDesktops is not { } desktops
            || !_focusByDesktop.TryGetValue((display.Handle, desktops.CurrentDesktopId), out var remembered))
        {
            return null;
        }

        foreach (var leaf in treeManager.LeavesOn(display))
        {
            if (leaf.Window.Handle != remembered)
            {
                continue;
            }

            // Alive as well as tiled. A window can sit in the tree for the interval between it dying
            // and reconciliation noticing, and activating a dead handle would leave the arrival with
            // no focus at all -- strictly worse than the first tile.
            return registry.TryGetWindow(remembered, out var window) && window is { IsAlive: true }
                ? leaf
                : null;
        }

        return null;
    }

    /// <summary>
    /// Puts focus back on <paramref name="handle"/> after a move the shell refused.
    /// </summary>
    /// <remarks>
    /// The mirror of handing focus on early. Focus really has moved by the time the shell answers,
    /// so a refusal has to be undone rather than merely not acted on -- and like every other
    /// activation here, the cache advances only if the activation actually worked.
    /// </remarks>
    private void RestoreFocusTo(nint handle)
    {
        if (registry.TryGetLeaf(handle, out var leaf) && leaf is not null)
        {
            if (registry.TryGetWindow(handle, out var window) && window is { IsAlive: true }
                && window.TryActivate())
            {
                _focused = leaf;
            }

            return;
        }

        // Untracked: no leaf, and so no IWindow either -- the whole path above cannot reach it.
        // The cache is deliberately NOT touched on the way out: it names a tile, and this window
        // has none, so writing anything here would be a claim the tree cannot back.
        ActivateUntrackedWindow?.Invoke(handle);
    }

    /// <summary>
    /// Hands focus to a window on the desktop now being viewed, for a switch CosmicWin did NOT make.
    /// </summary>
    /// <remarks>
    /// <c>Win+Ctrl+Left/Right</c> and Task View raise nothing this process subscribes to, so the
    /// only way to notice them is the reconciliation tick asking. The tick owns that comparison and
    /// calls this once it has seen the desktop change; the chord path answers itself, immediately,
    /// without waiting an interval.
    /// </remarks>
    public void HandFocusToArrivingDesktop() => HandFocusToVisibleDesktop(departingHandle: 0, arriving: true);

    /// <summary>
    /// Resolves the leaf currently treated as focused: the leaf the OS foreground actually maps to,
    /// and only if that window is untracked, the last leaf CosmicWin successfully activated.
    /// Returns <see langword="false"/> (no-op, never throws) when neither resolves — e.g. the
    /// foreground window is untracked and nothing has been focused yet, or the tree is empty.
    /// </summary>
    /// <remarks>
    /// MR-2 root cause: the cache used to be consulted FIRST and returned on
    /// nothing more than "still tracked and alive", so it never re-synced with the desktop. Paired
    /// with <see cref="MoveFocus"/> advancing it before knowing whether activation worked, a single
    /// failed <c>SetForegroundWindow</c> desynced CosmicWin's focus model permanently — every later
    /// chord then walked from a window the user was not on. The third supervised run's trace caught
    /// it directly: activation to <c>0x99030A</c> failed at 12:46:32, and the next chord ten seconds
    /// later still reported <c>focused=0x99030A</c>. The OS is the authority on focus; the cache
    /// only covers the case where the OS answer is useless to us (an untracked foreground window,
    /// e.g. a dialog or a non-tiled app), where dropping the chord entirely would be worse.
    /// </remarks>
    private bool TryResolveFocused(nint foregroundHandle, out LeafNode focused)
    {
        if (foregroundHandle != 0 && registry.TryGetLeaf(foregroundHandle, out var leaf) && leaf is not null)
        {
            _focused = leaf;
            focused = leaf;
            return true;
        }

        if (_focused is not null &&
            registry.TryGetWindow(_focused.Window.Handle, out var cached) && cached is { IsAlive: true })
        {
            focused = _focused;
            return true;
        }

        focused = null!;
        return false;
    }

    /// <summary>
    /// Last resort for a focus chord that resolved to nothing: puts the user back INTO the tree,
    /// reporting whether it answered the chord.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two readings in <see cref="TryResolveFocused"/> cover the case they were written for --
    /// a dialog or a non-tiled app stealing the foreground, where the tiled window BEHIND it is
    /// still in the tree and the cache names it. Neither can cover the window that LEFT the tree
    /// while being looked at, because then the foreground is untracked AND the cache holds the leaf
    /// that was just removed.
    /// </para>
    /// <para>
    /// Measured with NVIDIA Broadcast: tiled and focused, Alt+O made its tile shorter than its
    /// minimum size, and the adapter untiled it -- correctly. From that moment every layout chord
    /// died, reported as focused=0x0 UnresolvedFocus, with no way back except the mouse. The same
    /// symptom as the standing chord-dropout report, reached by a different route.
    /// </para>
    /// <para>
    /// It LANDS rather than walks, and that is the difference between a way in and a starting
    /// point. Walking from the survivor answers nothing when it is the only tile left -- which is
    /// exactly the shape a window leaving the tree tends to produce -- and a user pressing a
    /// direction from outside is asking to be back inside, not to travel from a tile they are not
    /// on. Precedent: the desktop handover activates its landing leaf for the same reason.
    /// </para>
    /// <para>
    /// The display is named by the tile the departed leaf last occupied, which survives the leaf
    /// leaving the tree and is where the user was working. With no cache at all -- nothing has ever
    /// been focused -- ResolveDisplay's documented Primary fail-safe answers.
    /// </para>
    /// <para>
    /// Direction chords only. Alt+[ and Alt+] ascend from a leaf the user is standing on and there
    /// is none out here; a direction is the way back in, and every other chord works again the
    /// moment it lands.
    /// </para>
    /// </remarks>
    private bool TryEnterTheTree(Direction direction, nint foregroundHandle)
    {
        if (TreeManager is not { } trees
            || ResolveWindowBounds?.Invoke(foregroundHandle) is not { } from
            || from.Width <= 0 || from.Height <= 0)
        {
            return false;
        }

        if (NearestTileToward(trees, direction, from) is not { } survivor)
        {
            return false;
        }

        var target = survivor.Window.Handle;
        if (!registry.TryGetWindow(target, out var window) || window is null)
        {
            Trace(direction, foregroundHandle, 0, target, FocusTraceOutcome.UntrackedTarget);
            return true;
        }

        // The cache advances only on a REAL activation, exactly as the ordinary walk does. A refused
        // SetForegroundWindow that still relocated CosmicWin's idea of focus is the MR-2 defect.
        var outcome = window.Activate();
        var activated = outcome.Confirmed();
        if (activated)
        {
            _focused = survivor;
            _focusScope = null;
        }

        Trace(direction, foregroundHandle, 0, target,
            activated ? FocusTraceOutcome.Activated : FocusTraceOutcome.ActivateFailed, outcome);

        return true;
    }

    /// <summary>
    /// The nearest tile lying in <paramref name="direction"/> from <paramref name="from"/>, or
    /// <see langword="null"/> when nothing lies that way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Geometry rather than a tree walk, because the window this measures from is NOT in the tree
    /// -- that is the whole situation -- so there is no leaf to walk from and no sibling order to
    /// read. Centres, the same comparison <c>TreeManager</c> already uses to find an adjacent
    /// display.
    /// </para>
    /// <para>
    /// The direction is not decoration and must be obeyed. A window evicted for refusing to be
    /// repositioned sits wherever it pinned itself, and handing FocusRight the only survivor put
    /// the user on a window physically to its LEFT -- a measured misdirection this codebase already
    /// pins with its own facts. Nothing in the direction pressed is a legitimate answer of "no",
    /// exactly as it is for a tile at the edge of the tree.
    /// </para>
    /// </remarks>
    private static LeafNode? NearestTileToward(TreeManager trees, Direction direction, Interop.Rectangle from)
    {
        var originX = from.Left + (from.Width / 2);
        var originY = from.Top + (from.Height / 2);

        LeafNode? nearest = null;
        var shortest = long.MaxValue;

        foreach (var leaf in trees.LeavesOn(trees.ResolveDisplay(from)))
        {
            var tile = leaf.LastGeometry;
            if (tile.Width <= 0 || tile.Height <= 0)
            {
                continue;
            }

            var x = tile.X + (tile.Width / 2);
            var y = tile.Y + (tile.Height / 2);

            var distance = direction switch
            {
                Direction.Left => originX - x,
                Direction.Right => x - originX,
                Direction.Up => originY - y,
                _ => y - originY,
            };

            if (distance > 0 && distance < shortest)
            {
                shortest = distance;
                nearest = leaf;
            }
        }

        return nearest;
    }

    /// <summary>
    /// LE-2 focus move: does not re-arrange (focus alone never changes tree geometry) — instead
    /// activates the newly focused window's real OS window ("focus activation").
    /// </summary>
    private void MoveFocus(Direction direction, LeafNode focused, nint foregroundHandle)
    {
        var origin = focused.Window.Handle;
        var (localEngine, _) = ResolveEngineAndWorkArea(focused);
        var result = localEngine.NextFocus(direction, focused);
        if (result.Status != FocusWalkStatus.Found || result.Leaf is null)
        {
            Trace(direction, foregroundHandle, origin, 0, FocusTraceOutcome.NoMatch);
            return;
        }

        var target = result.Leaf.Window.Handle;
        if (!registry.TryGetWindow(target, out var window) || window is null)
        {
            Trace(direction, foregroundHandle, origin, target, FocusTraceOutcome.UntrackedTarget);
            return;
        }

        // The cache advances only on a REAL activation. Moving it first
        // as this method used to -- meant a rejected SetForegroundWindow still relocated CosmicWin's
        // idea of focus, and nothing ever moved it back.
        // The RUNG, not just the bool. This is the ordinary same-desktop path, so its rung is what
        // the desktop handover's rung gets compared against -- and that comparison is the cheapest
        // way there is to falsify a hypothesis about the activation escalation.
        var outcome = window.Activate();
        var activated = outcome.Confirmed();
        if (activated)
        {
            _focused = result.Leaf;
            // Ascending is short-lived by design: landing on a new window puts the user back on a
            // leaf, so a forgotten Alt+[ cannot silently turn a later Move into a group move.
            _focusScope = null;
        }

        Trace(direction, foregroundHandle, origin, target,
            activated ? FocusTraceOutcome.Activated : FocusTraceOutcome.ActivateFailed,
            outcome);
    }

    /// <summary>
    /// Handles the virtual-desktop chords, returning whether this action was one. A no-op when no
    /// service is wired or the build is unsupported -- the chord is then simply consumed, which is
    /// what the user already sees for any action the tree cannot satisfy.
    /// </summary>
    /// <summary>
    /// Asks the foreground window to close. Answers the chord whether or not anything can act on
    /// it, so it never falls through to a path aimed at a different window.
    /// </summary>
    /// <remarks>
    /// The tree is deliberately NOT touched. <c>WM_CLOSE</c> is a request an application may
    /// refuse -- an unsaved document puts up its own dialog and stays exactly where it is -- so
    /// removing the leaf here would desync the layout from the screen on every refusal, and there
    /// is no event to put it back. The window actually leaving arrives on its own, through the
    /// destroy/hide path that already reflows the survivors.
    /// </remarks>
    private bool TryDispatchClose(HotkeyAction action, nint foregroundHandle)
    {
        if (action.Kind is not HotkeyActionKind.CloseWindow)
        {
            return false;
        }

        if (foregroundHandle != 0)
        {
            var asked = CloseWindowAt?.Invoke(foregroundHandle) ?? false;
            DesktopTrace?.Record($"close hwnd=0x{foregroundHandle:X} asked={asked}");
        }

        return true;
    }

    private bool TryDispatchDesktop(HotkeyAction action, nint foregroundHandle)
    {
        if (action.Kind is not (HotkeyActionKind.SwitchDesktop or HotkeyActionKind.MoveWindowToDesktop
            or HotkeyActionKind.CloseDesktop))
        {
            return false;
        }

        if (VirtualDesktops is not { } desktops)
        {
            DesktopTrace?.Record($"{action.Kind} {action.Argument} -- no service wired");
            return true;
        }

        // Answered BEFORE the readings below, because it invalidates every one of them. Closing a
        // desktop is Windows' own Win+Ctrl+F4 delivered as synthetic input: the shell replies with
        // an animation rather than a return value, so `count` and `index` read on the next line
        // would describe a desktop set that has not finished changing.
        //
        // Nothing else happens here on purpose -- no handover, no tree surgery. The reconciliation
        // tick already rehomes every tracked window on each pass and hands focus on when it notices
        // a desktop CosmicWin did not switch to; a close produces exactly that aftermath, and it is
        // the only observer positioned to see it AFTER the shell has settled.
        if (action.Kind == HotkeyActionKind.CloseDesktop)
        {
            var closed = desktops.TryCloseCurrentDesktop();
            // count-BEFORE, spelled out. These are read the instant the input is queued, so on a
            // successful close they still name the desktop being closed -- a bare `count=` here
            // reads like an outcome and is not one. What proves a close landed is the NEXT line
            // this log gets, or the tick's own handover.
            DesktopTrace?.Record(
                $"CloseDesktop asked={closed} supported={desktops.IsSupported} " +
                $"count-before={desktops.Count} index-before={desktops.CurrentIndex} " +
                $"error={desktops.LastError ?? "(none)"}");
            return true;
        }

        var countBefore = desktops.Count;
        var indexBefore = desktops.CurrentIndex;

        // Read BEFORE, because "the switch succeeded" and "the user actually went somewhere" are
        // different facts. TrySwitchTo reports success for the desktop already shown -- it returns
        // early rather than paying for a desktop-change animation nobody asked for -- and handing
        // focus on there would yank the user off their own window for nothing.
        var desktopBefore = desktops.CurrentDesktopId;

        bool ok;
        if (action.Kind == HotkeyActionKind.SwitchDesktop)
        {
            // Filed BEFORE the switch and unconditionally. Before, because afterwards the service
            // names the arriving desktop and the record would be filed under it. Unconditionally,
            // because a refused switch leaves the user exactly where this says they are, and a
            // switch to the desktop already shown re-files the same window under the same key --
            // both are writes that cost nothing and say something true.
            RememberFocusOn(desktopBefore, foregroundHandle);

            ok = desktops.TrySwitchTo(action.Argument);
            if (ok)
            {
                DesktopSwitched?.Invoke();

                // Only when the view actually moved, and AFTER the arriving layout has been
                // applied: the tree being searched has to be the one the user is now looking at.
                if (desktops.CurrentDesktopId != desktopBefore)
                {
                    HandFocusToVisibleDesktop(departingHandle: 0, arriving: true);
                }
            }
        }
        else
        {
            // FOCUS LEAVES BEFORE THE WINDOW DOES, and the order is the whole fix.
            //
            // Reported from real use: after sending the focused window away, BOTH it and the newly
            // focused window wore an accent border -- with CosmicWin's own border switched off, so
            // the paint was DWM's, not ours. Moving first and handing focus on afterwards meant the
            // departing window was already on another desktop and CLOAKED by the time anything else
            // was activated. It never received a deactivation it could repaint while anyone could
            // see it, so it kept wearing its active frame and arrived on the other desktop still
            // looking focused.
            //
            // Handing focus on first costs nothing when the move succeeds. When it does not, it
            // costs an activation to undo -- so it is only done when the move is not already known
            // to be impossible. The window is still in the tree here, which is what
            // HandFocusToVisibleDesktop's exclusion is for.
            //
            // The service refuses THREE things without asking the shell: an unrecognised Windows
            // build, no foreground window to send, and an index outside 1..MaxIndex. Only the first
            // two are tested here, because the third cannot happen -- every desktop chord carries
            // 1..9 and MaxIndex is 9. That is an invariant across two files rather than a property
            // of either, so it is pinned by a fact
            // (DesktopChordTests.EveryDesktopChord_CarriesAnArgumentTheServiceWillNotRefuseOnRange)
            // instead of being asserted here. Add a chord for a tenth desktop and that fact fails
            // before this guard silently starts paying two activations for an impossible move.
            //
            // NOT a free refusal, and the reason an earlier version of this comment was wrong: an
            // index beyond the number of desktops that currently EXIST. The service creates
            // desktops until the index exists, by design, so Alt+Shift+5 on a two-desktop machine
            // is an ordinary successful move.
            //
            // What is left is the shell refusing this PARTICULAR window, which cannot be known
            // without asking. That refusal does cost one activation each way, and the facts bound
            // it exactly rather than leaving it open-ended.
            var handedOff = desktops.IsSupported && foregroundHandle != 0;
            if (handedOff)
            {
                HandFocusToVisibleDesktop(foregroundHandle, arriving: false);
            }

            // The window the user is looking at, straight from the OS. Deliberately not the tracked
            // leaf: sending an untracked window to another desktop is still a legitimate ask.
            ok = desktops.TryMoveWindowTo(foregroundHandle, action.Argument);

            if (ok)
            {
                // Only after the shell confirms the window really moved. Rehoming on a FAILED move
                // would tear the window out of a layout it never actually left.
                WindowMovedToDesktop?.Invoke(foregroundHandle);
            }
            else if (handedOff)
            {
                // Undone only if it was actually done. The window never left, so the user must end
                // up back on it; handing them to a different tile as a souvenir of the shell's
                // refusal would be worse than the refusal itself.
                RestoreFocusTo(foregroundHandle);
            }
        }

        DesktopTrace?.Record(
            $"{action.Kind} arg={action.Argument} ok={ok} supported={desktops.IsSupported} " +
            $"count={countBefore}->{desktops.Count} index={indexBefore}->{desktops.CurrentIndex} " +
            $"hwnd=0x{foregroundHandle:X} error={desktops.LastError ?? "(none)"}");

        return true;
    }

    /// <summary>
    /// <paramref name="activation"/> defaults to null for every path that never asked a window to
    /// activate -- an unresolved focus, a tree walk with no match, an untracked target. Those lines
    /// must not carry a rung, because there was none.
    /// </summary>
    private void Trace(
        Direction direction, nint foregroundHandle, nint focusedHandle, nint targetHandle,
        FocusTraceOutcome outcome, ActivationOutcome? activation = null) =>
        FocusTrace?.Record(new FocusTraceEntry(
            direction, foregroundHandle, focusedHandle, targetHandle, outcome, activation));

    /// <summary>The direction a FOCUS chord carries, or null for every other action kind.</summary>
    private static Direction? FocusDirectionOf(HotkeyActionKind kind) => kind switch
    {
        HotkeyActionKind.FocusLeft => Direction.Left,
        HotkeyActionKind.FocusRight => Direction.Right,
        HotkeyActionKind.FocusUp => Direction.Up,
        HotkeyActionKind.FocusDown => Direction.Down,
        _ => null
    };

    /// <summary>
    /// Applies a tree mutation (Move/Toggle/Resize) and, only if it actually changed something,
    /// re-arranges and positions every live leaf via the shared <see cref="TreeArranger"/>
    /// (: <see cref="WorkspaceSessionAdapter"/> now applies the same
    /// arrange-and-position step after a window is added or removed) — on the SAME tree/work area
    /// <paramref name="focused"/> was just mutated on.
    /// </summary>
    private void MutateScope(LeafNode focused, Func<ITilingEngine, Node, bool> mutate)
    {
        var (localEngine, workArea) = ResolveEngineAndWorkArea(focused);
        if (!mutate(localEngine, ResolveScope(focused)))
        {
            return;
        }

        // Null on purpose, and the one call site that is right to pass it: every chord already ends
        // in AfterAction, which refreshes the border once from ScheduleAsync's finally -- so it
        // still runs when the tiling path throws. Handing the same callback here as well would
        // place the border twice for one chord and buy nothing.
        TreeArranger.ArrangeAndPosition(localEngine, registry, workArea, afterArrange: null);
    }

    /// <summary>
    /// HA-1 <c>Alt+[</c>: selects the parent of the current scope. A no-op at the tree root -- there
    /// is nothing above it -- and never re-arranges anything, since ascending changes only WHICH node
    /// the next mutation receives, not the layout.
    /// </summary>
    private void AscendScope(LeafNode focused)
    {
        if (ResolveScope(focused).Parent is { } parent)
        {
            _focusScope = parent;
        }
    }

    /// <summary>
    /// HA-1 <c>Alt+]</c>: undoes one <see cref="AscendScope"/> by stepping back down the path toward
    /// the focused leaf. A no-op once the scope is the leaf itself.
    /// </summary>
    private void DescendScope(LeafNode focused)
    {
        var scope = ResolveScope(focused);
        if (ReferenceEquals(scope, focused))
        {
            return;
        }

        // The child of `scope` that the focused leaf sits under -- walking UP from the leaf, since
        // ResolveScope has already guaranteed the scope IS one of its ancestors.
        Node child = focused;
        while (child.Parent is { } parent && !ReferenceEquals(parent, scope))
        {
            child = parent;
        }

        _focusScope = child;
    }

    /// <summary>
    /// The scope to act on, defaulting to <paramref name="focused"/> itself. A remembered scope is
    /// honoured ONLY while the focused leaf is still somewhere beneath it; otherwise it is stale --
    /// the user clicked a window in another branch, or the tree was reshaped under it -- and applying
    /// it would mutate an unrelated subtree.
    /// </summary>
    private Node ResolveScope(LeafNode focused)
    {
        if (_focusScope is { } scope && IsAncestorOrSelf(scope, focused))
        {
            return scope;
        }

        _focusScope = null;
        return focused;
    }

    private static bool IsAncestorOrSelf(Node candidate, LeafNode leaf)
    {
        for (Node? node = leaf; node is not null; node = node.Parent)
        {
            if (ReferenceEquals(node, candidate))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves <paramref name="focused"/>'s OWN monitor tree/work area via
    /// <see cref="TreeManager"/> — <see cref="TreeManager.ResolveDisplay"/> is safe to reuse here
    /// because a tracked window's real <c>Bounds</c> still reflects whichever monitor it is
    /// physically on, even mid-desync. Falls back to the primary <paramref name="engine"/>/<see
    /// cref="WorkArea"/> when <see cref="TreeManager"/> is unset, the window is untracked, or its
    /// resolved display no longer has a tree.
    /// </summary>
    private (ITilingEngine Engine, Rect WorkArea) ResolveEngineAndWorkArea(LeafNode focused)
    {
        if (TreeManager is { } treeManager &&
            registry.TryGetWindow(focused.Window.Handle, out var window) && window is not null)
        {
            var display = treeManager.ResolveDisplay(window.Bounds);
            if (treeManager.TryGetTree(display, out var tree) && tree is not null)
            {
                return (tree, WorkAreaResolver.Resolve(display));
            }
        }

        return (engine, WorkArea);
    }
}
