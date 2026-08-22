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

    /// <summary>The monitor work area <see cref="ITilingEngine.Arrange"/> lays leaves out into.</summary>
    public Rect WorkArea { get; set; }

    /// <summary>
    /// WU18 (closes verify-report #21 V17-W1): when set, every mutation resolves and arranges
    /// the FOCUSED window's OWN monitor tree/work area, instead of always the primary <paramref
    /// name="engine"/>/<see cref="WorkArea"/> — restoring tree/screen agreement on secondary
    /// monitors. Null preserves the pre-WU18, primary-only behavior, so tests that construct this
    /// class directly without a monitor topology stay unaffected.
    /// </summary>
    public TreeManager? TreeManager { get; set; }

    /// <summary>
    /// MR-2 diagnosis (Engram discovery #101): when set, every FOCUS chord records what actually
    /// happened along the focus path -- the leaf it started from, the tree-walk result, the target
    /// handle and whether activation succeeded. Focus chords are the only ones traced; Move/Resize
    /// are known to work and stay silent. Null keeps the executor entirely untraced, so every test
    /// and call site that does not care is unaffected.
    /// </summary>
    public IFocusTrace? FocusTrace { get; set; }

    public ValueTask ScheduleAsync(HotkeyAction action, CancellationToken cancellationToken)
    {
        var foregroundHandle = foreground.GetForegroundHandle();
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
    /// MR-2 root cause (Engram discovery #104): the cache used to be consulted FIRST and returned on
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

        // The cache advances only on a REAL activation (Engram discovery #104). Moving it first --
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
    /// (verify-report #21 CRITICAL C2: <see cref="WorkspaceSessionAdapter"/> now applies the same
    /// arrange-and-position step after a window is added or removed) — on the SAME tree/work area
    /// <paramref name="focused"/> was just mutated on (WU18, closes V17-W1).
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
    /// WU18 (closes V17-W1): resolves <paramref name="focused"/>'s OWN monitor tree/work area via
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
