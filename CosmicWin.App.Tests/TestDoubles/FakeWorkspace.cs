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

    private readonly List<IWindow> _open = [];

    /// <summary>
    /// The windows this workspace has announced and not yet withdrawn.
    /// </summary>
    /// <remarks>
    /// It used to be permanently empty, which was true enough while nothing read it. The adapter
    /// now does: a window it untiled is no longer in its own bookkeeping, so the only way back to
    /// the <see cref="IWindow"/> behind a handle is to ask the workspace what is open. A double
    /// that answers "nothing" would make that lookup silently find no window and the behaviour
    /// under test never happen.
    /// </remarks>
    public IReadOnlyList<IWindow> Snapshot => _open;

    public void Open() => IsOpen = true;

    /// <summary>WT-1: how many times the reconciliation pass asked this workspace to catch up.</summary>
    public int PollCallCount { get; private set; }

    public void Poll()
    {
        // No native source to reconcile against in the fake -- the COUNT is the fact under test:
        // production must actually drive this, which until WT-1 was wired it never did.
        PollCallCount++;
    }

    /// <param name="arrival">
    /// Whether the real workspace would be reporting a birth or an adoption. Defaults to the birth
    /// every caller written before the distinction existed meant.
    /// </param>
    public void RaiseWindowAdded(IWindow window, WindowArrival arrival = WindowArrival.Created)
    {
        if (!_open.Contains(window))
        {
            _open.Add(window);
        }

        WindowAdded?.Invoke(this, new WindowEventArgs(window, arrival: arrival));
    }

    public void RaiseWindowRemoved(IWindow window)
    {
        _open.Remove(window);
        WindowRemoved?.Invoke(this, new WindowEventArgs(window));
    }

    /// <summary>Simulates the real <c>Win32Workspace</c> raising <see cref="WindowBoundsChanged"/> after an out-of-band move.</summary>
    /// <param name="isUserGesture">
    /// Whether this is the settled result of the user's own drag or resize, which is what the real
    /// workspace reports from MOVESIZEEND and the only bounds change allowed to reshape the tree.
    /// Defaults to the out-of-band case every caller before it meant.
    /// </param>
    public void RaiseWindowBoundsChanged(IWindow window, bool isUserGesture = false) =>
        WindowBoundsChanged?.Invoke(this, new WindowEventArgs(window, isUserGesture));

    public void Dispose()
    {
    }
}
