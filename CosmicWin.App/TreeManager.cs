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
    private readonly Dictionary<nint, LayoutTree> _trees = new();
    private nint _primaryHandle;

    public TreeManager(IReadOnlyList<IDisplay> displays, IDisplay primary, WindowRegistry registry)
    {
        _registry = registry;

        foreach (var display in displays)
        {
            _displays[display.Handle] = display;
            _trees[display.Handle] = new LayoutTree();
        }

        _primaryHandle = primary.Handle;
    }

    /// <summary><see langword="false"/> for an unknown/disconnected monitor handle.</summary>
    public bool TryGetTree(IDisplay display, out LayoutTree? tree) => _trees.TryGetValue(display.Handle, out tree);

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
        _trees[display.Handle] = new LayoutTree();
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
        if (!_trees.TryGetValue(display.Handle, out var tree) || tree is null)
        {
            return;
        }

        if (display.Handle == _primaryHandle)
        {
            throw new InvalidOperationException(
                "Cannot disconnect the current primary display; call SetPrimary with the " +
                "OS-reassigned new primary first.");
        }

        var primaryTree = _trees[_primaryHandle];
        ReparentLeaves(tree, primaryTree, primaryWorkArea);

        _displays.Remove(display.Handle);
        _trees.Remove(display.Handle);

        TreeArranger.ArrangeAndPosition(primaryTree, _registry, primaryWorkArea);
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
        if (!_trees.TryGetValue(display.Handle, out var tree) || tree is null)
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
        var target = FindAdjacentDisplay(current, direction);
        if (target is null || !_trees.TryGetValue(target.Handle, out var tree) || tree?.Root is null)
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
