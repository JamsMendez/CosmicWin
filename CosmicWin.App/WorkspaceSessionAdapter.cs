using CosmicWin.Interop;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App;

/// <summary>
/// Task 2.20 (WU7D-corrected, R3-001-corrected): bridges an <see cref="IWorkspace"/>'s add/remove
/// events into the shared <see cref="LayoutTree"/> and <see cref="WindowRegistry"/>, converting
/// <see cref="IWindow.Handle"/> to <see cref="WindowRef"/> at the Interop-&gt;Layout boundary
/// (<c>CosmicWin.Layout</c> stays Win32-free). <see cref="ITilingEngine"/> has no
/// <c>Insert</c>/<c>Remove(WindowRef)</c> -- only static <see
/// cref="LayoutTree.AddChild(LeafNode,WindowRef,int,int)"/>/<see cref="LayoutTree.RemoveChild"/>
/// plus mutable <see cref="Node.Parent"/> -- so this adapter owns root/parent bookkeeping
/// directly. This adapter owns one flat root group -- third and later windows append into the
/// existing <see cref="GroupNode"/> root rather than being dropped; multi-monitor and nested-tree
/// policy remain Phase 3 <c>TreeManager</c> scope.
/// </summary>
/// <remarks>
/// Fixes verify-report #21 CRITICAL C2 and WARNING W1. C2: every add/remove now re-arranges the
/// tree and positions each live leaf via the shared <see cref="TreeArranger"/> -- previously this
/// adapter only mutated the tree and never called <see cref="ITilingEngine.Arrange"/> or <see
/// cref="IWindow.SetPosition"/> at all. The constructor's <c>workArea</c> parameter is a
/// delegate rather than a captured value so this adapter always reads the exact same work area <see
/// cref="ActionExecutor.WorkArea"/> holds (single source of truth shared between both call
/// sites, wired by <see cref="App.OnStartup"/>). W1: LE-4's split-region heuristic is now sourced
/// from the existing leaf's last-arranged geometry (falling back to the work area before that
/// leaf has ever been arranged), not from the newly-arriving window's own <see
/// cref="IWindow.Bounds"/>.
/// </remarks>
public sealed class WorkspaceSessionAdapter : IDisposable
{
    private readonly IWorkspace _workspace;
    private readonly LayoutTree _tree;
    private readonly WindowRegistry _registry;
    private readonly Func<Rect> _workArea;
    private readonly Func<ExceptionList> _exceptions;
    private readonly Func<bool> _isPaused;

    /// <summary>
    /// Task 3.32: <paramref name="exceptions"/> mirrors <paramref name="workArea"/>'s single-source-of-truth delegate pattern (2.27) -- a later Reload (WE-3) takes effect with no re-wiring.
    /// Task 3.15/3.16 (WU11), settled full-pause semantics: <paramref name="isPaused"/> mirrors the
    /// same idiom -- while it reports <c>true</c>, a newly-created window is NOT auto-tiled (same
    /// creation-time-only, forward-only rule as exclusion). Optional and defaults to never-paused so
    /// existing call sites that predate the tray (WU11) keep compiling unchanged.
    /// </summary>
    public WorkspaceSessionAdapter(
        IWorkspace workspace, LayoutTree tree, WindowRegistry registry, Func<Rect> workArea,
        Func<ExceptionList> exceptions, Func<bool>? isPaused = null)
    {
        _workspace = workspace;
        _tree = tree;
        _registry = registry;
        _workArea = workArea;
        _exceptions = exceptions;
        _isPaused = isPaused ?? (() => false);

        _workspace.WindowAdded += OnWindowAdded;
        _workspace.WindowRemoved += OnWindowRemoved;
    }

    /// <summary>The tree root this adapter keeps synchronized with <see cref="_workspace"/>.</summary>
    public Node? Root => _tree.Root;

    private void OnWindowAdded(object? sender, WindowEventArgs e)
    {
        // Task 3.15/3.16 (WU11): settled full-pause semantics -- a window opened while paused is
        // NOT auto-tiled, and is not retroactively pulled in once Reanudar is clicked (same
        // creation-time-only, forward-only rule the exclusion guard below already applies).
        if (_isPaused())
        {
            return;
        }

        var window = e.Window;

        // Task 3.32: spec WT-3/WE-1/WE-2, closes verify-report #21 N1. Creation-time only -- an
        // already-tracked window newly excluded by a later Reload is NOT retroactively removed
        // (WU10 documented scope boundary).
        var descriptor = WindowDescriptorBuilder.Build(window);
        if (WindowFilters.IsExcluded(descriptor, _exceptions()))
        {
            return;
        }

        var windowRef = new WindowRef(window.Handle);

        switch (_tree.Root)
        {
            case null:
                var root = new LeafNode(windowRef);
                _tree.Root = root;
                _registry.Register(window, root);
                break;

            case LeafNode existingLeaf:
                // W1 fix: LE-4 requires the region actually being split -- the existing leaf's
                // last-arranged geometry, or the work area before it has ever been arranged --
                // not the newly-arriving window's own Bounds.
                var region = existingLeaf.LastGeometry is { Width: > 0, Height: > 0 } geometry
                    ? geometry
                    : _workArea();
                var group = LayoutTree.AddChild(existingLeaf, windowRef, region.Width, region.Height);
                _tree.Root = group;
                // AddChild builds its own LeafNode internally -- register that exact instance.
                var insertedLeaf = (LeafNode)group.Children[^1];
                _registry.Register(window, insertedLeaf);
                break;

            case GroupNode existingGroup:
                var newLeaf = new LeafNode(windowRef);
                LayoutTree.AddChild(existingGroup, newLeaf, existingGroup.Children.Count);
                _registry.Register(window, newLeaf);
                break;

            default:
                return;
        }

        TreeArranger.ArrangeAndPosition(_tree, _registry, _workArea());
    }

    private void OnWindowRemoved(object? sender, WindowEventArgs e)
    {
        var handle = e.Window.Handle;
        if (!_registry.TryGetLeaf(handle, out var leaf) || leaf is null)
        {
            return;
        }

        if (leaf.Parent is { } parent)
        {
            var index = parent.Children.IndexOf(leaf);
            if (index >= 0)
            {
                LayoutTree.RemoveChild(parent, index);
            }
        }
        else if (ReferenceEquals(_tree.Root, leaf))
        {
            _tree.Root = null;
        }

        _registry.Remove(handle);

        // V11-W2 (WU11-W2): settled full-pause semantics, "full pause, no reconcile" (decision #64).
        // Removing the node above always happens -- no dead handle is left in the tree while paused
        // -- but the reflow that would reposition the surviving windows is skipped entirely, and NOT
        // fired retroactively on resume (no resume hook exists anywhere in this adapter). The
        // resulting tree/screen desync is accepted behavior for TC-2, not a defect.
        if (_isPaused())
        {
            return;
        }

        TreeArranger.ArrangeAndPosition(_tree, _registry, _workArea());
    }

    public void Dispose()
    {
        _workspace.WindowAdded -= OnWindowAdded;
        _workspace.WindowRemoved -= OnWindowRemoved;
    }
}
