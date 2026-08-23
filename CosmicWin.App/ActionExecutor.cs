using CosmicWin.App.Diagnostics;
using CosmicWin.App.Input;
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
        var foregroundHandle = foreground.GetForegroundHandle();

        // Desktop chords are answered BEFORE focus is resolved. They are about which desktop the
        // user is looking at, not about the tiling tree, and they must keep working when the
        // foreground window is one CosmicWin does not track at all -- a dialog, an excluded app, or
        // a desktop that happens to be empty.
        if (TryDispatchDesktop(action, foregroundHandle))
        {
            return ValueTask.CompletedTask;
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

            return ValueTask.CompletedTask;
        }

        if (TryResolveFocused(foregroundHandle, out var focused))
        {
            Dispatch(action.Kind, focused, foregroundHandle);
        }
        else if (FocusDirectionOf(action.Kind) is { } direction)
        {
            // The chord never reached the tree walk: recorded rather than dropped, so a silent
            // focus chord on real hardware can be told apart from a failed one -- and the
            // foreground handle names the untracked window that was holding focus instead.
            Trace(direction, foregroundHandle, 0, 0, FocusTraceOutcome.UnresolvedFocus);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Handles a move chord for a window the tree does not contain, reporting whether anyone owned
    /// it. Unset -- as in every test that predates floating dialogs -- such a chord is simply dropped.
    /// </summary>
    public Func<nint, Direction, bool>? MoveFloatingWindow { get; set; }

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
        var activated = window.TryActivate();
        if (activated)
        {
            _focused = result.Leaf;
            // Ascending is short-lived by design: landing on a new window puts the user back on a
            // leaf, so a forgotten Alt+[ cannot silently turn a later Move into a group move.
            _focusScope = null;
        }

        Trace(direction, foregroundHandle, origin, target,
            activated ? FocusTraceOutcome.Activated : FocusTraceOutcome.ActivateFailed);
    }

    /// <summary>
    /// Handles the virtual-desktop chords, returning whether this action was one. A no-op when no
    /// service is wired or the build is unsupported -- the chord is then simply consumed, which is
    /// what the user already sees for any action the tree cannot satisfy.
    /// </summary>
    private bool TryDispatchDesktop(HotkeyAction action, nint foregroundHandle)
    {
        if (action.Kind is not (HotkeyActionKind.SwitchDesktop or HotkeyActionKind.MoveWindowToDesktop))
        {
            return false;
        }

        if (VirtualDesktops is not { } desktops)
        {
            DesktopTrace?.Record($"{action.Kind} {action.Argument} -- no service wired");
            return true;
        }

        var countBefore = desktops.Count;
        var indexBefore = desktops.CurrentIndex;

        // The window the user is looking at, straight from the OS. Deliberately not the tracked
        // leaf: sending an untracked window to another desktop is still a legitimate ask.
        var ok = action.Kind == HotkeyActionKind.SwitchDesktop
            ? desktops.TrySwitchTo(action.Argument)
            : desktops.TryMoveWindowTo(foregroundHandle, action.Argument);

        // Only after the shell confirms the window really moved. Rehoming on a FAILED move would
        // tear the window out of a layout it never actually left.
        if (ok && action.Kind == HotkeyActionKind.MoveWindowToDesktop)
        {
            WindowMovedToDesktop?.Invoke(foregroundHandle);
        }
        else if (ok && action.Kind == HotkeyActionKind.SwitchDesktop)
        {
            DesktopSwitched?.Invoke();
        }

        DesktopTrace?.Record(
            $"{action.Kind} arg={action.Argument} ok={ok} supported={desktops.IsSupported} " +
            $"count={countBefore}->{desktops.Count} index={indexBefore}->{desktops.CurrentIndex} " +
            $"hwnd=0x{foregroundHandle:X} error={desktops.LastError ?? "(none)"}");

        return true;
    }

    private void Trace(
        Direction direction, nint foregroundHandle, nint focusedHandle, nint targetHandle, FocusTraceOutcome outcome) =>
        FocusTrace?.Record(new FocusTraceEntry(direction, foregroundHandle, focusedHandle, targetHandle, outcome));

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

        TreeArranger.ArrangeAndPosition(localEngine, registry, workArea);
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
