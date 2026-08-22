using System.Linq;
using CosmicWin.Interop;
using CosmicWin.Layout;

namespace CosmicWin.App;

/// <summary>
/// Design D4 / Phase 3 (tasks 3.1-3.8, spec MM-1..MM-5): owns one <see cref="LayoutTree"/> per
/// connected monitor (MM-1); connect creates an empty tree (MM-2); disconnect reparents into the
/// primary tree (MM-3); a DPI/work-area change reflows only that monitor (MM-4); MM-5 resolves
/// cross-monitor focus fallthrough when LE-2's in-tree walk finds no match.
/// </summary>
/// <remarks>
/// Deliberate scope boundary (documented, not silent, see sdd/cosmic-win/apply-progress): this
/// batch does not wire <see cref="TreeManager"/> into <see cref="CompositionRoot"/>/
/// <see cref="App"/> -- that requires <see cref="ActionExecutor"/>/
/// <see cref="WorkspaceSessionAdapter"/> to become monitor-aware, out of this batch's scope.
/// </remarks>
public sealed class TreeManager
{
    private readonly WindowRegistry _registry;
    private readonly Dictionary<nint, IDisplay> _displays = new();

    /// <summary>
    /// One tree per (monitor, virtual desktop). Nested rather than a composite key so every
    /// existing monitor operation -- connect, disconnect, work-area change -- still addresses a
    /// monitor directly and simply fans out across its desktops.
    /// </summary>
    /// <remarks>
    /// The desktop dimension was added after switching desktops was measured rearranging the
    /// windows on the desktop returned to. The eviction that caused it is fixed in
    /// <c>Win32Workspace.Poll</c>, but that alone only makes the tree SURVIVE: with a single tree,
    /// every desktop's windows are laid out together. A layout belongs to a desktop, so the model
    /// has to say so.
    /// </remarks>
    private readonly Dictionary<nint, Dictionary<Guid, LayoutTree>> _trees = new();
    private nint _primaryHandle;

    public TreeManager(IReadOnlyList<IDisplay> displays, IDisplay primary, WindowRegistry registry)
    {
        _registry = registry;

        foreach (var display in displays)
        {
            _displays[display.Handle] = display;
            _trees[display.Handle] = new Dictionary<Guid, LayoutTree>();
        }

        _primaryHandle = primary.Handle;
    }

    /// <summary>
    /// Which virtual desktop the user is on. Unset means "there is only one", which is exactly how
    /// every caller that predates virtual desktops behaves -- they address <see cref="Guid.Empty"/>
    /// and never notice the extra dimension.
    /// </summary>
    public Func<Guid>? CurrentDesktop { get; set; }

    /// <summary>The tree for <paramref name="display"/> on the desktop currently being viewed.</summary>
    public bool TryGetTree(IDisplay display, out LayoutTree? tree) =>
        TryGetTree(CurrentDesktop?.Invoke() ?? Guid.Empty, display, out tree);

    /// <summary>
    /// The tree for a SPECIFIC desktop, created on first use. Desktops appear while the app runs,
    /// so they cannot be pre-created the way monitors are -- and a window may need filing under a
    /// desktop the user is not currently looking at.
    /// </summary>
    /// <returns><see langword="false"/> only for an unknown/disconnected monitor handle.</returns>
    public bool TryGetTree(Guid desktop, IDisplay display, out LayoutTree? tree)
    {
        tree = null;
        if (!_trees.TryGetValue(display.Handle, out var byDesktop))
        {
            return false;
        }

        if (!byDesktop.TryGetValue(desktop, out tree))
        {
            tree = new LayoutTree();
            byDesktop[desktop] = tree;
        }

        return true;
    }

    /// <summary>Every desktop's tree for one monitor, for operations that must not miss a hidden one.</summary>
    private IEnumerable<LayoutTree> TreesOn(nint displayHandle) =>
        _trees.TryGetValue(displayHandle, out var byDesktop) ? byDesktop.Values : [];

    /// <summary>The display currently treated as primary (see <see cref="SetPrimary"/>).</summary>
    public IDisplay Primary => _displays[_primaryHandle];

    /// <summary>WU17 (closes W3, MM-1): resolves which connected monitor <paramref name="windowBounds"/> belongs to (whose <see cref="IDisplay.Bounds"/> contains the window's center), falling back to <see cref="Primary"/> when none match (e.g. an off-screen rect), mirroring MM-3's reparent-into-primary fail-safe.</summary>
    public IDisplay ResolveDisplay(Rectangle windowBounds)
    {
        var centerX = windowBounds.Left + windowBounds.Width / 2;
        var centerY = windowBounds.Top + windowBounds.Height / 2;

        foreach (var display in _displays.Values)
        {
            var bounds = display.Bounds;
            if (centerX >= bounds.Left && centerX < bounds.Right &&
                centerY >= bounds.Top && centerY < bounds.Bottom)
            {
                return display;
            }
        }

        return Primary;
    }

    /// <summary>
    /// MM-2: a newly-connected monitor gets a fresh, empty tree. No-op if the handle is already
    /// known (e.g. a duplicate connect notification) -- never replaces an existing tree, so an
    /// already-populated tree's windows are never lost.
    /// </summary>
    public void OnDisplayConnected(IDisplay display)
    {
        if (_trees.ContainsKey(display.Handle))
        {
            return;
        }

        _displays[display.Handle] = display;
        _trees[display.Handle] = new Dictionary<Guid, LayoutTree>();
    }

    /// <summary>
    /// MM-3: reparents every window from <paramref name="display"/>'s tree into the primary tree
    /// (appended, no orphaned-tree preservation), preserving the exact <see cref="LeafNode"/>
    /// instances already in <see cref="WindowRegistry"/> (never recreated), then removes
    /// <paramref name="display"/>'s entry and re-arranges/positions the primary tree via the
    /// shared <see cref="TreeArranger"/>. No-op for an unknown/already-removed handle. Throws if
    /// <paramref name="display"/> is the current primary -- MM-3's scenario only covers a
    /// secondary disconnecting; callers must call <see cref="SetPrimary"/> with the
    /// OS-reassigned new primary first.
    /// </summary>
    public void OnDisplayDisconnected(IDisplay display, Rect primaryWorkArea)
    {
        if (!_trees.TryGetValue(display.Handle, out var byDesktop))
        {
            return;
        }

        if (display.Handle == _primaryHandle)
        {
            throw new InvalidOperationException(
                "Cannot disconnect the current primary display; call SetPrimary with the " +
                "OS-reassigned new primary first.");
        }

        // Every desktop's windows come across, each into the SAME desktop on the primary. Taking
        // only the visible one would silently strand the layouts the user cannot currently see.
        foreach (var (desktop, tree) in byDesktop)
        {
            var primary = _displays[_primaryHandle];
            TryGetTree(desktop, primary, out var primaryTree);
            ReparentLeaves(tree, primaryTree!, primaryWorkArea);
        }

        _displays.Remove(display.Handle);
        _trees.Remove(display.Handle);

        if (TryGetTree(_displays[_primaryHandle], out var visible) && visible is not null)
        {
            TreeArranger.ArrangeAndPosition(visible, _registry, primaryWorkArea);
        }
    }

    /// <summary>Updates which known display is treated as primary (see <see cref="OnDisplayDisconnected"/>).</summary>
    public void SetPrimary(IDisplay display) => _primaryHandle = display.Handle;

    private static void ReparentLeaves(LayoutTree source, LayoutTree destination, Rect fallbackRegion)
    {
        foreach (var leaf in CollectLeaves(source.Root))
        {
            InsertExistingNode(destination, leaf, fallbackRegion);
        }

        source.Root = null;
    }

    private static List<LeafNode> CollectLeaves(Node? node) => node switch
    {
        null => [],
        LeafNode leaf => [leaf],
        GroupNode group => group.Children.SelectMany(CollectLeaves).ToList(),
        _ => throw new InvalidOperationException($"Unknown node type: {node.GetType()}")
    };

    private static void InsertExistingNode(LayoutTree destination, Node node, Rect fallbackRegion)
    {
        node.Parent = null;
        switch (destination.Root)
        {
            case null:
                destination.Root = node;
                break;

            case LeafNode existingLeaf:
                var region = existingLeaf.LastGeometry is { Width: > 0, Height: > 0 } geometry
                    ? geometry
                    : fallbackRegion;
                destination.Root = LayoutTree.AddChild(existingLeaf, node, region.Width, region.Height);
                break;

            case GroupNode existingGroup:
                LayoutTree.AddChild(existingGroup, node, existingGroup.Children.Count);
                break;
        }
    }

    /// <summary>
    /// MM-4: reflows only <paramref name="display"/>'s tree via the shared <see
    /// cref="TreeArranger"/> -- other monitors' trees are untouched, so a DPI/work-area change on
    /// one monitor never repositions another monitor's windows. No-op for an unknown handle.
    /// </summary>
    public void OnDisplayChanged(IDisplay display, Rect workArea)
    {
        // Only the VISIBLE desktop's tree is repositioned. The hidden ones will be arranged on the
        // work area in force when the user returns to them, so laying them out now would move
        // windows nobody can see, and to geometry that may be stale again by then.
        if (!TryGetTree(display, out var tree) || tree is null)
        {
            return;
        }

        TreeArranger.ArrangeAndPosition(tree, _registry, workArea);
    }

    /// <summary>
    /// MM-5: when LE-2's in-tree walk reaches root with no match, resolves the nearest connected
    /// monitor whose center lies in <paramref name="direction"/> from <paramref name="current"/>,
    /// returning its tree's first leaf (depth-first). Geometric ranking applies only BETWEEN
    /// monitors -- LE-2's own in-tree walk stays tree-walk-only (spec LE-2 step 4 boundary).
    /// </summary>
    public FocusResult FocusAdjacentDisplay(IDisplay current, Direction direction)
    {
        // The VISIBLE tree on that monitor: focus can only fall through to a window the user can
        // actually see, never to one parked on a desktop they are not looking at.
        var target = FindAdjacentDisplay(current, direction);
        if (target is null || !TryGetTree(target, out var tree) || tree?.Root is null)
        {
            return FocusResult.NoMatch;
        }

        return FocusResult.Found(FirstLeaf(tree.Root));
    }

    private IDisplay? FindAdjacentDisplay(IDisplay current, Direction direction)
    {
        var (fromX, fromY) = Center(current.Bounds);
        IDisplay? best = null;
        long bestDistance = long.MaxValue;

        foreach (var candidate in _displays.Values)
        {
            if (candidate.Handle == current.Handle)
            {
                continue;
            }

            var (toX, toY) = Center(candidate.Bounds);
            long distance = direction switch
            {
                Direction.Left when toX < fromX => fromX - toX,
                Direction.Right when toX > fromX => toX - fromX,
                Direction.Up when toY < fromY => fromY - toY,
                Direction.Down when toY > fromY => toY - fromY,
                _ => -1
            };

            if (distance >= 0 && distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return best;
    }

    private static (long X, long Y) Center(Rectangle bounds) =>
        (bounds.Left + bounds.Width / 2L, bounds.Top + bounds.Height / 2L);

    private static LeafNode FirstLeaf(Node node) => node switch
    {
        LeafNode leaf => leaf,
        GroupNode { Children.Count: > 0 } group => FirstLeaf(group.Children[0]),
        _ => throw new InvalidOperationException("Cannot descend into an empty group.")
    };
}
