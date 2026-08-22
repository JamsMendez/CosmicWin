using CosmicWin.Interop;
using CosmicWin.Layout;
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
/// WU21 (decision #80, supersedes WU19's/WU20's own "tree follows window" choice, never put to the
/// user): a dragged window SNAPS BACK to its tree slot on drop -- the tree is the source of truth;
/// windows move between slots/monitors only via a hotkey (spec LE-5). A plain re-arrange of the
/// window's OWN tree undoes any drag by construction, since <see cref="TreeArranger"/> never reads
/// on-screen position -- removes WU19's cross-monitor re-home and WU20's reorder outright, rather
/// than fixing them further. Gated by <see cref="_isPaused"/> exactly like decision #76.
/// </remarks>
/// <remarks>
/// WU23 (closes CRITICAL V22-W1, decision #83): the "evict a window that refuses repositioning"
/// guard WU22 wired into <see cref="OnWindowBoundsChanged"/> alone now lives inside the shared
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
    private readonly Dictionary<nint, IDisplay> _owners = new();

    /// <param name="focusedLeaf">
    /// LE-4: the tile a newly arriving window splits. Mandatory rather than optional -- a dropped
    /// focus source does not fail, it silently reverts to appending every window to the end of the
    /// row, which is exactly the defect this parameter exists to close.
    /// </param>
    public MultiMonitorWorkspaceAdapter(
        IWorkspace workspace, TreeManager treeManager, WindowRegistry registry,
        Func<ExceptionList> exceptions, Func<bool> isPaused, Func<LeafNode?> focusedLeaf)
    {
        _focusedLeaf = focusedLeaf;
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
        WorkspaceSessionAdapter.InsertWindow(tree, _registry, workArea, window, _focusedLeaf());
        _owners[window.Handle] = display;

        TreeArranger.ArrangeAndPosition(tree, _registry, workArea);

        // V22-W1: the choke point above evicts a window that fails ITS OWN first positioning
        // attempt (e.g. a protected window that never accepts a reposition) from the tree and
        // registry -- clean up this adapter's own per-window bookkeeping to match, since
        // TreeArranger has no access to it.
        if (!window.CanReposition)
        {
            _owners.Remove(window.Handle);
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
    /// Decision #80 (WU21): snaps an already-tracked window back to its tree slot after any
    /// out-of-band move. No-op while paused or for an untracked/excluded window. Ignores the new
    /// real position entirely -- re-arranging the UNCHANGED tree re-applies the same geometry,
    /// undoing the drag on screen, cross-monitor included (returns to its ORIGINAL slot).
    /// </summary>
    /// <remarks>
    /// V21-W1 / V22-W1 (decisions #81/#83): the snap-back attempt above can itself fail -- a window
    /// whose <see cref="IWindow.SetPosition"/> refuses to move flips <see
    /// cref="IWindow.CanReposition"/> to <c>false</c> permanently (one-way, never self-heals per its
    /// documented contract). Left alone, that window keeps the drag forever while the tree still
    /// treats it as being in its old slot -- desyncing tree order from screen order for good (the
    /// measured defect: a dragged-past sibling becomes the wrong `FocusRight` target). Decided
    /// reading for a window ALREADY in the tree that starts refusing repositioning: treat it
    /// exactly like a WE-1 exclusion -- evict it from the tree/registry (untileable, left floating
    /// where it is) and reflow the remaining siblings into the space it vacated, mirroring the
    /// design threat-matrix row "Cross-process window manipulation" (leaf marked untileable, never
    /// retried in a loop). WU23: the actual eviction now happens INSIDE <see
    /// cref="TreeArranger.ArrangeAndPosition"/> (the shared choke point, closes V22-W1) -- this
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
            // Tracked but no longer tileable (a minimise). Measured 2026-08-22: left in the tree it
            // keeps a full tile while drawing nothing, which is what made one visible window occupy
            // only half the screen. Remove it and reflow the survivors into the space.
            _owners.Remove(handle);
            if (WorkspaceSessionAdapter.RemoveWindow(tree, _registry, handle))
            {
                TreeArranger.ArrangeAndPosition(tree, _registry, WorkAreaResolver.Resolve(display));
            }

            return;
        }

        TreeArranger.ArrangeAndPosition(tree, _registry, WorkAreaResolver.Resolve(display));

        if (!window.CanReposition)
        {
            _owners.Remove(handle);
        }
    }

    public void Dispose()
    {
        _workspace.WindowAdded -= OnWindowAdded;
        _workspace.WindowRemoved -= OnWindowRemoved;
        _workspace.WindowBoundsChanged -= OnWindowBoundsChanged;
    }
}
