using CosmicWin.Interop;
using CosmicWin.Layout;

namespace CosmicWin.App;

/// <summary>
/// Task 2.20: bridges an <see cref="IWorkspace"/>'s add/remove events into the shared <see
/// cref="LayoutTree"/> and <see cref="WindowRegistry"/>, converting <see cref="IWindow.Handle"/>
/// to <see cref="WindowRef"/> at the Interop-&gt;Layout boundary (<c>CosmicWin.Layout</c> stays
/// Win32-free). <see cref="ITilingEngine"/> has no <c>Insert</c>/<c>Remove(WindowRef)</c> -- only
/// static <see cref="LayoutTree.AddChild(LeafNode,WindowRef,int,int)"/>/<see
/// cref="LayoutTree.RemoveChild"/> plus mutable <see cref="Node.Parent"/> -- so this adapter owns
/// root/parent bookkeeping directly. Scoped to a single flat tree; a root already a <see
/// cref="GroupNode"/> when a third+ window arrives is Phase 3 <c>TreeManager</c> scope, left
/// untouched here rather than guessed at.
/// </summary>
public sealed class WorkspaceSessionAdapter : IDisposable
{
    private readonly IWorkspace _workspace;
    private readonly LayoutTree _tree;
    private readonly WindowRegistry _registry;

    public WorkspaceSessionAdapter(IWorkspace workspace, LayoutTree tree, WindowRegistry registry)
    {
        _workspace = workspace;
        _tree = tree;
        _registry = registry;

        _workspace.WindowAdded += OnWindowAdded;
        _workspace.WindowRemoved += OnWindowRemoved;
    }

    /// <summary>The tree root this adapter keeps synchronized with <see cref="_workspace"/>.</summary>
    public Node? Root => _tree.Root;

    private void OnWindowAdded(object? sender, WindowEventArgs e)
    {
        var window = e.Window;
        var windowRef = new WindowRef(window.Handle);

        switch (_tree.Root)
        {
            case null:
                var root = new LeafNode(windowRef);
                _tree.Root = root;
                _registry.Register(window, root);
                break;

            case LeafNode existingLeaf:
                var region = window.Bounds;
                var group = LayoutTree.AddChild(existingLeaf, windowRef, region.Width, region.Height);
                _tree.Root = group;
                // AddChild builds its own LeafNode internally -- register that exact instance.
                var insertedLeaf = (LeafNode)group.Children[^1];
                _registry.Register(window, insertedLeaf);
                break;

            default:
                return; // 3rd+ window: out of this work unit's scope (see summary).
        }
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
    }

    public void Dispose()
    {
        _workspace.WindowAdded -= OnWindowAdded;
        _workspace.WindowRemoved -= OnWindowRemoved;
    }
}
