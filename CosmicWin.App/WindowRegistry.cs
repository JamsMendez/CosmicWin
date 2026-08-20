using CosmicWin.Interop;
using CosmicWin.Layout;

namespace CosmicWin.App;

/// <summary>
/// App-owned mapping between a native HWND, the tracked <see cref="IWindow"/>, and its
/// <see cref="LeafNode"/> in the (Win32-free) layout tree — design: "App owns the HWND↔IWindow
/// ↔LeafNode registry and activation orchestration". Keeps <c>CosmicWin.Layout</c> Win32-free by
/// never storing an <see cref="IWindow"/> reference inside the tree itself.
/// </summary>
public sealed class WindowRegistry
{
    private readonly Dictionary<nint, IWindow> _windows = new();
    private readonly Dictionary<nint, LeafNode> _leaves = new();

    /// <summary>
    /// Registers the mapping for <paramref name="window"/>'s handle, replacing any existing
    /// mapping for the same handle (unique HWND↔IWindow↔LeafNode — task 2.9).
    /// </summary>
    public void Register(IWindow window, LeafNode leaf)
    {
        _windows[window.Handle] = window;
        _leaves[window.Handle] = leaf;
    }

    /// <summary>Removes any mapping for <paramref name="handle"/>. Returns whether one existed.</summary>
    public bool Remove(nint handle)
    {
        var removedWindow = _windows.Remove(handle);
        var removedLeaf = _leaves.Remove(handle);
        return removedWindow || removedLeaf;
    }

    /// <summary><see langword="false"/> for a stale/unknown handle — never throws.</summary>
    public bool TryGetWindow(nint handle, out IWindow? window) => _windows.TryGetValue(handle, out window);

    /// <summary><see langword="false"/> for a stale/unknown handle — never throws.</summary>
    public bool TryGetLeaf(nint handle, out LeafNode? leaf) => _leaves.TryGetValue(handle, out leaf);
}
