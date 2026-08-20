using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;
using CosmicWin.Layout;

namespace CosmicWin.App.Tests;

/// <summary>Task 2.9: unique HWND↔IWindow↔LeafNode registry, replacement/removal, stale lookup.</summary>
public sealed class WindowRegistryTests
{
    [Fact]
    public void Register_ThenLookup_ReturnsSameWindowAndLeaf()
    {
        var registry = new WindowRegistry();
        var window = new RecordingWindow(new IntPtr(1), Rectangle.FromSize(0, 0, 100, 100));
        var leaf = new LeafNode(new WindowRef(window.Handle));

        registry.Register(window, leaf);

        Assert.True(registry.TryGetWindow(window.Handle, out var foundWindow));
        Assert.Same(window, foundWindow);
        Assert.True(registry.TryGetLeaf(window.Handle, out var foundLeaf));
        Assert.Same(leaf, foundLeaf);
    }

    [Fact]
    public void Register_SameHandleTwice_ReplacesPreviousMapping()
    {
        var registry = new WindowRegistry();
        var handle = new IntPtr(2);
        var firstWindow = new RecordingWindow(handle, Rectangle.FromSize(0, 0, 100, 100));
        registry.Register(firstWindow, new LeafNode(new WindowRef(handle)));

        var secondWindow = new RecordingWindow(handle, Rectangle.FromSize(10, 10, 50, 50));
        var secondLeaf = new LeafNode(new WindowRef(handle));
        registry.Register(secondWindow, secondLeaf);

        Assert.True(registry.TryGetWindow(handle, out var foundWindow));
        Assert.Same(secondWindow, foundWindow);
        Assert.True(registry.TryGetLeaf(handle, out var foundLeaf));
        Assert.Same(secondLeaf, foundLeaf);
    }

    [Fact]
    public void Remove_ExistingHandle_ClearsBothMappings_AndReturnsTrue()
    {
        var registry = new WindowRegistry();
        var handle = new IntPtr(3);
        var window = new RecordingWindow(handle, Rectangle.FromSize(0, 0, 100, 100));
        registry.Register(window, new LeafNode(new WindowRef(handle)));

        var removed = registry.Remove(handle);

        Assert.True(removed);
        Assert.False(registry.TryGetWindow(handle, out _));
        Assert.False(registry.TryGetLeaf(handle, out _));
    }

    [Fact]
    public void Remove_UnknownHandle_ReturnsFalse()
    {
        var registry = new WindowRegistry();

        Assert.False(registry.Remove(new IntPtr(999)));
    }

    [Fact]
    public void TryGetWindow_StaleOrUnknownHandle_ReturnsFalse_WithoutThrowing()
    {
        var registry = new WindowRegistry();

        var exception = Record.Exception(() => registry.TryGetWindow(new IntPtr(404), out _));

        Assert.Null(exception);
        Assert.False(registry.TryGetWindow(new IntPtr(404), out var window));
        Assert.Null(window);
    }
}
