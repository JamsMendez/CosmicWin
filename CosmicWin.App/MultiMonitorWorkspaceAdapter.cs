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
/// Declared scope boundary (see sdd/cosmic-win/apply-progress), not hidden: <see
/// cref="ActionExecutor"/> still operates on only the primary monitor's tree -- unchanged from
/// today -- so MM-5 focus fallthrough and cross-monitor hotkey move/resize/toggle stay unwired.
/// MM-2/MM-3/MM-4's live hotplug/DPI-change triggers also stay unwired: no such Win32 event source
/// exists yet in <c>CosmicWin.Interop</c>.
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

    public void Dispose()
    {
        _workspace.WindowAdded -= OnWindowAdded;
        _workspace.WindowRemoved -= OnWindowRemoved;
    }
}
