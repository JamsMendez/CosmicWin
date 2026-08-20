using CosmicWin.Interop;

namespace CosmicWin.Interop.Tests.TestDoubles;

/// <summary>
/// Hand-rolled in-memory <see cref="IWorkspace"/> used to pin down the contract before the
/// Win32-backed implementation exists.
/// </summary>
internal sealed class FakeWorkspace : IWorkspace
{
    private readonly Dictionary<IntPtr, FakeWindow> _windows = new();

    public event EventHandler<WindowEventArgs>? WindowAdded;
    public event EventHandler<WindowEventArgs>? WindowRemoved;
    public event EventHandler<WindowEventArgs>? WindowBoundsChanged;

    public bool IsOpen { get; private set; }

    public IReadOnlyList<IWindow> Snapshot => _windows.Values.Cast<IWindow>().ToArray();

    public void Open() => IsOpen = true;

    public void Poll()
    {
        // No native source to reconcile against in the fake — Win32Workspace's poll
        // fallback is exercised against a fake native gateway instead (see Win32/).
    }

    public FakeWindow AddWindow(IntPtr handle, string title, Rectangle bounds)
    {
        var window = new FakeWindow(handle, title, bounds);
        _windows[handle] = window;
        WindowAdded?.Invoke(this, new WindowEventArgs(window));
        return window;
    }

    public void RemoveWindow(IntPtr handle)
    {
        if (_windows.Remove(handle, out var window))
        {
            window.Kill();
            WindowRemoved?.Invoke(this, new WindowEventArgs(window));
        }
    }

    public void MoveWindow(IntPtr handle, Rectangle bounds)
    {
        if (_windows.TryGetValue(handle, out var window))
        {
            window.SetPosition(bounds);
            WindowBoundsChanged?.Invoke(this, new WindowEventArgs(window));
        }
    }

    public void Dispose()
    {
    }
}
