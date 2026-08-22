using CosmicWin.Interop;

namespace CosmicWin.App.Tests.TestDoubles;

/// <summary>
/// Minimal <see cref="IWorkspace"/> double that only raises the events
/// <see cref="WorkspaceSessionAdapter"/> subscribes to. Mirrors the Interop.Tests
/// <c>FakeWorkspace</c> shape without depending on that internal type across assemblies.
/// </summary>
internal sealed class FakeWorkspace : IWorkspace
{
    public event EventHandler<WindowEventArgs>? WindowAdded;
    public event EventHandler<WindowEventArgs>? WindowRemoved;

    public event EventHandler<WindowEventArgs>? WindowBoundsChanged;

    public bool IsOpen { get; private set; }

    public IReadOnlyList<IWindow> Snapshot => Array.Empty<IWindow>();

    public void Open() => IsOpen = true;

    /// <summary>WT-1: how many times the reconciliation pass asked this workspace to catch up.</summary>
    public int PollCallCount { get; private set; }

    public void Poll()
    {
        // No native source to reconcile against in the fake -- the COUNT is the fact under test:
        // production must actually drive this, which until WT-1 was wired it never did.
        PollCallCount++;
    }

    public void RaiseWindowAdded(IWindow window) => WindowAdded?.Invoke(this, new WindowEventArgs(window));

    public void RaiseWindowRemoved(IWindow window) => WindowRemoved?.Invoke(this, new WindowEventArgs(window));

    /// <summary>Simulates the real <c>Win32Workspace</c> raising <see cref="WindowBoundsChanged"/> after an out-of-band move (verify-report #21 V18-W2).</summary>
    public void RaiseWindowBoundsChanged(IWindow window) => WindowBoundsChanged?.Invoke(this, new WindowEventArgs(window));

    public void Dispose()
    {
    }
}
