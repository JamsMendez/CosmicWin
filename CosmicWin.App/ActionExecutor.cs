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

    /// <summary>The monitor work area <see cref="ITilingEngine.Arrange"/> lays leaves out into.</summary>
    public Rect WorkArea { get; set; }

    public ValueTask ScheduleAsync(HotkeyAction action, CancellationToken cancellationToken)
    {
        if (TryResolveFocused(out var focused))
        {
            Dispatch(action.Kind, focused);
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
            case HotkeyActionKind.MoveLeft: MutateAndArrange(() => engine.MoveNode(Direction.Left, focused)); break;
            case HotkeyActionKind.MoveRight: MutateAndArrange(() => engine.MoveNode(Direction.Right, focused)); break;
            case HotkeyActionKind.MoveUp: MutateAndArrange(() => engine.MoveNode(Direction.Up, focused)); break;
            case HotkeyActionKind.MoveDown: MutateAndArrange(() => engine.MoveNode(Direction.Down, focused)); break;
            case HotkeyActionKind.ToggleOrientation: MutateAndArrange(() => engine.ToggleAxis(focused)); break;
            case HotkeyActionKind.ResizeLeft: MutateAndArrange(() => engine.ResizeNode(Direction.Left, focused)); break;
            case HotkeyActionKind.ResizeRight: MutateAndArrange(() => engine.ResizeNode(Direction.Right, focused)); break;
            case HotkeyActionKind.ResizeUp: MutateAndArrange(() => engine.ResizeNode(Direction.Up, focused)); break;
            case HotkeyActionKind.ResizeDown: MutateAndArrange(() => engine.ResizeNode(Direction.Down, focused)); break;
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
        var result = engine.NextFocus(direction, focused);
        if (result.Status != FocusWalkStatus.Found || result.Leaf is null)
        {
            return;
        }

        _focused = result.Leaf;
        if (registry.TryGetWindow(result.Leaf.Window.Handle, out var window) && window is not null)
        {
            window.TryActivate();
        }
    }

    /// <summary>
    /// Applies a tree mutation (Move/Toggle/Resize) and, only if it actually changed something,
    /// re-arranges the whole tree via <see cref="ITilingEngine.Arrange"/> and positions every
    /// live, tracked leaf's real window -- one <see cref="IWindow.SetPosition"/> per leaf,
    /// respecting <see cref="IWindow.CanReposition"/>'s no-retry contract.
    /// </summary>
    private void MutateAndArrange(Func<bool> mutate)
    {
        if (!mutate())
        {
            return;
        }

        foreach (var (windowRef, bounds) in engine.Arrange(WorkArea))
        {
            if (!registry.TryGetWindow(windowRef.Handle, out var window) || window is not { IsAlive: true })
            {
                continue;
            }

            window.SetPosition(Rectangle.FromSize(bounds.X, bounds.Y, bounds.Width, bounds.Height));
        }
    }
}
