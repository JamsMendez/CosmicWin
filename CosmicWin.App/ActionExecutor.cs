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
        if (TryResolveFocused(out var focused))
        {
            Dispatch(action.Kind, focused);
        }
        else if (FocusDirectionOf(action.Kind) is { } direction)
        {
            // The chord never reached the tree walk: recorded rather than dropped, so a silent
            // focus chord on real hardware can be told apart from a failed one.
            Trace(direction, 0, 0, FocusTraceOutcome.UnresolvedFocus);
        }

        return ValueTask.CompletedTask;
    }

    private void Dispatch(HotkeyActionKind kind, LeafNode focused)
    {
        switch (kind)
        {
            case HotkeyActionKind.FocusLeft: MoveFocus(Direction.Left, focused); break;
            case HotkeyActionKind.FocusRight: MoveFocus(Direction.Right, focused); break;
            case HotkeyActionKind.FocusUp: MoveFocus(Direction.Up, focused); break;
            case HotkeyActionKind.FocusDown: MoveFocus(Direction.Down, focused); break;
            case HotkeyActionKind.MoveLeft: MutateAndArrange(focused, e => e.MoveNode(Direction.Left, focused)); break;
            case HotkeyActionKind.MoveRight: MutateAndArrange(focused, e => e.MoveNode(Direction.Right, focused)); break;
            case HotkeyActionKind.MoveUp: MutateAndArrange(focused, e => e.MoveNode(Direction.Up, focused)); break;
            case HotkeyActionKind.MoveDown: MutateAndArrange(focused, e => e.MoveNode(Direction.Down, focused)); break;
            case HotkeyActionKind.ToggleOrientation: MutateAndArrange(focused, e => e.ToggleAxis(focused)); break;
            case HotkeyActionKind.ResizeLeft: MutateAndArrange(focused, e => e.ResizeNode(Direction.Left, focused)); break;
            case HotkeyActionKind.ResizeRight: MutateAndArrange(focused, e => e.ResizeNode(Direction.Right, focused)); break;
            case HotkeyActionKind.ResizeUp: MutateAndArrange(focused, e => e.ResizeNode(Direction.Up, focused)); break;
            case HotkeyActionKind.ResizeDown: MutateAndArrange(focused, e => e.ResizeNode(Direction.Down, focused)); break;
            case HotkeyActionKind.FocusIn:
            case HotkeyActionKind.FocusOut:
                // Deferred: Phase 1's ITilingEngine exposes no nested-group descend/ascend
                // primitive (design note on ITilingEngine — root ownership/window lookup policy
                // is App-layer scope, not yet defined). No-op rather than a crash; tracked as a
                // documented deviation, not silently dropped behavior.
                break;
        }
    }

    /// <summary>
    /// Resolves the leaf currently treated as focused: the cached leaf if it is still tracked and
    /// alive, otherwise a fresh OS foreground-window lookup mapped through <see
    /// cref="WindowRegistry"/>. Returns <see langword="false"/> (no-op, never throws) when neither
    /// resolves — e.g. the foreground window is untracked or the tree is empty.
    /// </summary>
    private bool TryResolveFocused(out LeafNode focused)
    {
        if (_focused is not null &&
            registry.TryGetWindow(_focused.Window.Handle, out var cached) && cached is { IsAlive: true })
        {
            focused = _focused;
            return true;
        }

        var handle = foreground.GetForegroundHandle();
        if (handle != 0 && registry.TryGetLeaf(handle, out var leaf) && leaf is not null)
        {
            _focused = leaf;
            focused = leaf;
            return true;
        }

        focused = null!;
        return false;
    }

    /// <summary>
    /// LE-2 focus move: does not re-arrange (focus alone never changes tree geometry) — instead
    /// activates the newly focused window's real OS window ("focus activation").
    /// </summary>
    private void MoveFocus(Direction direction, LeafNode focused)
    {
        var origin = focused.Window.Handle;
        var (localEngine, _) = ResolveEngineAndWorkArea(focused);
        var result = localEngine.NextFocus(direction, focused);
        if (result.Status != FocusWalkStatus.Found || result.Leaf is null)
        {
            Trace(direction, origin, 0, FocusTraceOutcome.NoMatch);
            return;
        }

        _focused = result.Leaf;
        var target = result.Leaf.Window.Handle;
        if (!registry.TryGetWindow(target, out var window) || window is null)
        {
            Trace(direction, origin, target, FocusTraceOutcome.UntrackedTarget);
            return;
        }

        var activated = window.TryActivate();
        Trace(direction, origin, target,
            activated ? FocusTraceOutcome.Activated : FocusTraceOutcome.ActivateFailed);
    }

    private void Trace(Direction direction, nint focusedHandle, nint targetHandle, FocusTraceOutcome outcome) =>
        FocusTrace?.Record(new FocusTraceEntry(direction, focusedHandle, targetHandle, outcome));

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
    private void MutateAndArrange(LeafNode focused, Func<ITilingEngine, bool> mutate)
    {
        var (localEngine, workArea) = ResolveEngineAndWorkArea(focused);
        if (!mutate(localEngine))
        {
            return;
        }

        TreeArranger.ArrangeAndPosition(localEngine, registry, workArea);
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
