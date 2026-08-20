using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;
using CosmicWin.Layout;

namespace CosmicWin.App.Tests;

/// <summary>
/// Task 2.19: <see cref="WorkspaceSessionAdapter"/> syncs the shared tree/registry with
/// <c>WindowAdded</c>/<c>WindowRemoved</c> -- first window becomes a lone leaf root, a second
/// splits it via <c>AddChild</c>, removal walks <c>Node.Parent</c> into <c>RemoveChild</c>, and
/// removing the sole remaining window clears the root.
/// </summary>
public sealed class WorkspaceSessionAdapterTests
{
    [Fact]
    public void WindowAdded_FirstWindow_SetsLoneLeafRoot_AndRegistersInWindowRegistry()
    {
        var workspace = new FakeWorkspace();
        var tree = new LayoutTree();
        var registry = new WindowRegistry();
        using var adapter = new WorkspaceSessionAdapter(workspace, tree, registry);

        var window = new RecordingWindow(new IntPtr(1), Rectangle.FromSize(0, 0, 800, 600));
        workspace.RaiseWindowAdded(window);

        var leaf = Assert.IsType<LeafNode>(tree.Root);
        Assert.Equal(new WindowRef(window.Handle), leaf.Window);
        Assert.Same(leaf, adapter.Root);
        Assert.True(registry.TryGetWindow(window.Handle, out var foundWindow));
        Assert.Same(window, foundWindow);
        Assert.True(registry.TryGetLeaf(window.Handle, out var foundLeaf));
        Assert.Same(leaf, foundLeaf);
    }

    [Fact]
    public void WindowAdded_SecondWindow_SplitsExistingLeafIntoGroup_AndRegistersNewLeaf()
    {
        var workspace = new FakeWorkspace();
        var tree = new LayoutTree();
        var registry = new WindowRegistry();
        using var adapter = new WorkspaceSessionAdapter(workspace, tree, registry);

        var first = new RecordingWindow(new IntPtr(10), Rectangle.FromSize(0, 0, 1920, 1080));
        workspace.RaiseWindowAdded(first);
        var firstLeaf = Assert.IsType<LeafNode>(tree.Root);

        var second = new RecordingWindow(new IntPtr(20), Rectangle.FromSize(0, 0, 1920, 1080));
        workspace.RaiseWindowAdded(second);

        var group = Assert.IsType<GroupNode>(tree.Root);
        Assert.Equal(2, group.Children.Count);
        Assert.Same(firstLeaf, group.Children[0]);
        var newLeaf = Assert.IsType<LeafNode>(group.Children[1]);
        Assert.Equal(new WindowRef(second.Handle), newLeaf.Window);
        Assert.Equal(group.GroupLength, group.Sizes.Sum());
        Assert.True(registry.TryGetWindow(second.Handle, out var foundSecond));
        Assert.Same(second, foundSecond);
        Assert.True(registry.TryGetLeaf(second.Handle, out var foundLeaf));
        Assert.Same(newLeaf, foundLeaf);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void WindowAdded_ThirdOrLaterWindow_AppendsAndRegistersExactLeaf_WithValidGroup(int windowCount)
    {
        var workspace = new FakeWorkspace();
        var tree = new LayoutTree();
        var registry = new WindowRegistry();
        using var adapter = new WorkspaceSessionAdapter(workspace, tree, registry);
        var windows = Enumerable.Range(1, windowCount)
            .Select(index => new RecordingWindow(
                new IntPtr(index * 10),
                Rectangle.FromSize(0, 0, 1920, 1080)))
            .ToArray();

        foreach (var window in windows)
        {
            workspace.RaiseWindowAdded(window);
        }

        var group = Assert.IsType<GroupNode>(tree.Root);
        Assert.Equal(windowCount, group.Children.Count);
        Assert.Equal(group.Children.Count, group.Sizes.Count);
        Assert.Equal(group.GroupLength, group.Sizes.Sum());
        Assert.All(group.Children, child => Assert.Same(group, child.Parent));
        var insertedLeaf = Assert.IsType<LeafNode>(group.Children[^1]);
        Assert.Equal(new WindowRef(windows[^1].Handle), insertedLeaf.Window);
        Assert.True(registry.TryGetWindow(windows[^1].Handle, out var registeredWindow));
        Assert.Same(windows[^1], registeredWindow);
        Assert.True(registry.TryGetLeaf(windows[^1].Handle, out var registeredLeaf));
        Assert.Same(insertedLeaf, registeredLeaf);
    }

    [Fact]
    public void WindowRemoved_ThirdOrLaterWindow_UnregistersAndPreservesValidGroup()
    {
        var workspace = new FakeWorkspace();
        var tree = new LayoutTree();
        var registry = new WindowRegistry();
        using var adapter = new WorkspaceSessionAdapter(workspace, tree, registry);
        var windows = Enumerable.Range(1, 4)
            .Select(index => new RecordingWindow(
                new IntPtr(index * 10),
                Rectangle.FromSize(0, 0, 1920, 1080)))
            .ToArray();
        foreach (var window in windows)
        {
            workspace.RaiseWindowAdded(window);
        }
        var removedLeaf = Assert.IsType<LeafNode>(
            Assert.IsType<GroupNode>(tree.Root).Children[2]);

        workspace.RaiseWindowRemoved(windows[2]);

        var group = Assert.IsType<GroupNode>(tree.Root);
        Assert.Equal(3, group.Children.Count);
        Assert.Equal(group.Children.Count, group.Sizes.Count);
        Assert.Equal(group.GroupLength, group.Sizes.Sum());
        Assert.All(group.Children, child => Assert.Same(group, child.Parent));
        Assert.DoesNotContain(removedLeaf, group.Children);
        Assert.Null(removedLeaf.Parent);
        Assert.False(registry.TryGetWindow(windows[2].Handle, out _));
        Assert.False(registry.TryGetLeaf(windows[2].Handle, out _));
        Assert.All(
            windows.Where((_, index) => index != 2),
            window => Assert.True(registry.TryGetLeaf(window.Handle, out _)));
    }

    [Fact]
    public void WindowRemoved_WithSibling_WalksNodeParent_RemovesChildFromGroup_AndUnregisters()
    {
        var workspace = new FakeWorkspace();
        var tree = new LayoutTree();
        var registry = new WindowRegistry();
        using var adapter = new WorkspaceSessionAdapter(workspace, tree, registry);

        var first = new RecordingWindow(new IntPtr(30), Rectangle.FromSize(0, 0, 1920, 1080));
        workspace.RaiseWindowAdded(first);
        var second = new RecordingWindow(new IntPtr(40), Rectangle.FromSize(0, 0, 1920, 1080));
        workspace.RaiseWindowAdded(second);

        workspace.RaiseWindowRemoved(second);

        var group = Assert.IsType<GroupNode>(tree.Root);
        var remainingLeaf = Assert.IsType<LeafNode>(Assert.Single(group.Children));
        Assert.Equal(new WindowRef(first.Handle), remainingLeaf.Window);
        Assert.Equal(group.GroupLength, group.Sizes.Sum());
        Assert.False(registry.TryGetWindow(second.Handle, out _));
        Assert.False(registry.TryGetLeaf(second.Handle, out _));
    }

    [Fact]
    public void WindowRemoved_SoleRemainingWindow_ClearsRoot()
    {
        var workspace = new FakeWorkspace();
        var tree = new LayoutTree();
        var registry = new WindowRegistry();
        using var adapter = new WorkspaceSessionAdapter(workspace, tree, registry);

        var only = new RecordingWindow(new IntPtr(50), Rectangle.FromSize(0, 0, 800, 600));
        workspace.RaiseWindowAdded(only);

        workspace.RaiseWindowRemoved(only);

        Assert.Null(tree.Root);
        Assert.Null(adapter.Root);
        Assert.False(registry.TryGetWindow(only.Handle, out _));
        Assert.False(registry.TryGetLeaf(only.Handle, out _));
    }
}
