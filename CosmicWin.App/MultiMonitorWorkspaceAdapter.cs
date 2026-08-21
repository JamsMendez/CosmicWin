using CosmicWin.Interop;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App;

/// <summary>
/// WU17: closes carried finding W3 (spec MM-1..MM-5, design D4) -- the first real production
/// caller of <see cref="TreeManager"/>. Reuses <see cref="WorkspaceSessionAdapter"/>'s extracted
/// <see cref="WorkspaceSessionAdapter.InsertWindow"/>/<see cref="WorkspaceSessionAdapter.RemoveWindow"/>/
/// <see cref="WorkspaceSessionAdapter.IsExcluded"/> statics (never a drifting second copy), but
/// resolves each window's owning monitor via <see cref="TreeManager.ResolveDisplay"/> instead of a
/// single fixed tree (MM-1), so add/remove/reflow apply only to the affected monitor's tree (MM-4's
/// isolation property).
/// </summary>
/// <remarks>
/// WU18 (closes V17-W1): <see cref="ActionExecutor"/> is now <see cref="TreeManager"/>-aware --
/// a hotkey mutation on a secondary monitor's focused window arranges that SAME secondary tree on
/// its own work area, so tree and screen no longer desync after Move/Resize/Toggle. Cross-monitor
/// MOVEMENT via a HOTKEY (moving a window from one monitor's tree to another's with Move/Resize/
/// Toggle) remains out of scope, per design: <see cref="CosmicWin.Layout.LayoutTree.MoveNode"/>
/// operates strictly inside <c>focused.Parent</c>. MM-5 focus fallthrough and MM-2/MM-3/MM-4's
/// live hotplug/DPI-change triggers stay unwired: no such Win32 event source exists yet in
/// <c>CosmicWin.Interop</c>.
/// </remarks>
/// <remarks>
/// WU19 (closes verify-report #21 V18-W2, spec WT-1's move/resize tracking clause): subscribes
/// the already-existing, already-tested <see cref="IWorkspace.WindowBoundsChanged"/> so an
/// OUT-OF-BAND move (a mouse drag, not a hotkey) re-homes the leaf into whichever monitor's tree
/// its real position now belongs to, instead of leaving it structurally rooted in its OLD tree --
/// the exact desync that reproduces V17-W1's transcript on the next hotkey. Design choice (spec
/// WT-1 mandates the WM TRACK a move, not fight it): the tree is updated to follow the window,
/// never the reverse -- a drag is never snapped back to its pre-drag tree slot. Gated by the same
/// <see cref="_isPaused"/> check as <see cref="OnWindowAdded"/>, and to the SAME degree: while
/// paused, a drag is a full no-op (no re-home, no reflow), not merely a reflow-skip -- the most
/// conservative reading available, since decision #64's "full pause, no reconcile" text speaks
/// only to reflow and does not settle whether structural tree state may still shift under a paused
/// user; this class does not let it.
/// </remarks>
public sealed class MultiMonitorWorkspaceAdapter : IDisposable
{
    private readonly IWorkspace _workspace;
    private readonly TreeManager _treeManager;
    private readonly WindowRegistry _registry;
    private readonly Func<ExceptionList> _exceptions;
    private readonly Func<bool> _isPaused;
    private readonly Dictionary<nint, IDisplay> _owners = new();

    public MultiMonitorWorkspaceAdapter(
        IWorkspace workspace, TreeManager treeManager, WindowRegistry registry,
        Func<ExceptionList> exceptions, Func<bool> isPaused)
    {
        _workspace = workspace;
        _treeManager = treeManager;
        _registry = registry;
        _exceptions = exceptions;
        _isPaused = isPaused;

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

        var display = _treeManager.ResolveDisplay(window.Bounds);
        if (!_treeManager.TryGetTree(display, out var tree) || tree is null)
        {
            return;
        }

        var workArea = WorkAreaResolver.Resolve(display);
        WorkspaceSessionAdapter.InsertWindow(tree, _registry, workArea, window);
        _owners[window.Handle] = display;

        TreeArranger.ArrangeAndPosition(tree, _registry, workArea);
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
        if (!WorkspaceSessionAdapter.RemoveWindow(tree, _registry, handle))
        {
            return;
        }

        // V11-W2 (decision #64, "full pause, no reconcile"), re-proven for this TreeManager-routed
        // adapter: removal itself always happens; only the reflow of the AFFECTED monitor's tree
        // is skipped while paused, and never fired retroactively on resume.
        if (_isPaused())
        {
            return;
        }

        TreeArranger.ArrangeAndPosition(tree, _registry, WorkAreaResolver.Resolve(display));
    }

    /// <summary>
    /// V18-W2 closure: re-homes an already-tracked leaf when its real on-screen position no longer
    /// matches the monitor whose tree structurally contains it (an out-of-band drag). No-op while
    /// paused (full skip, see the class remarks), for an untracked/excluded window, or when the
    /// window's resolved display has not actually changed (a same-monitor drag needs no re-home).
    /// </summary>
    private void OnWindowBoundsChanged(object? sender, WindowEventArgs e)
    {
        if (_isPaused())
        {
            return;
        }

        var window = e.Window;
        var handle = window.Handle;
        if (!_owners.TryGetValue(handle, out var oldDisplay))
        {
            return;
        }

        var newDisplay = _treeManager.ResolveDisplay(window.Bounds);
        if (newDisplay.Handle == oldDisplay.Handle)
        {
            return;
        }

        if (!_treeManager.TryGetTree(oldDisplay, out var oldTree) || oldTree is null ||
            !_treeManager.TryGetTree(newDisplay, out var newTree) || newTree is null)
        {
            return;
        }

        if (!WorkspaceSessionAdapter.RemoveWindow(oldTree, _registry, handle))
        {
            return;
        }

        var newWorkArea = WorkAreaResolver.Resolve(newDisplay);
        WorkspaceSessionAdapter.InsertWindow(newTree, _registry, newWorkArea, window);
        _owners[handle] = newDisplay;

        TreeArranger.ArrangeAndPosition(oldTree, _registry, WorkAreaResolver.Resolve(oldDisplay));
        TreeArranger.ArrangeAndPosition(newTree, _registry, newWorkArea);
    }

    public void Dispose()
    {
        _workspace.WindowAdded -= OnWindowAdded;
        _workspace.WindowRemoved -= OnWindowRemoved;
        _workspace.WindowBoundsChanged -= OnWindowBoundsChanged;
    }
}
