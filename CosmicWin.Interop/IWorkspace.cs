namespace CosmicWin.Interop;

/// <summary>
/// Carries the <see cref="IWindow"/> a workspace event refers to. Shaped after the reference implementation's
/// <c>WindowChangedEventArgs</c> shape.
/// </summary>
public sealed class WindowEventArgs : EventArgs
{
    public WindowEventArgs(IWindow window, bool isUserGesture = false)
    {
        Window = window;
        IsUserGesture = isUserGesture;
    }

    public IWindow Window { get; }

    /// <summary>
    /// This event is the settled result of the user dragging or resizing the window by hand,
    /// rather than any other bounds change (a restore, a shell nudge, an app resizing itself).
    /// </summary>
    /// <remarks>
    /// The two are the same fact about geometry and a completely different fact about INTENT: a
    /// hand-resize is the user answering "how big should this be", and it is the only bounds change
    /// that carries an answer. Everything else arriving on the same event has nothing to say about
    /// the layout, so a listener that rewrote the tree from every one of them would be inventing
    /// the user's intent out of a window minimising itself.
    /// </remarks>
    public bool IsUserGesture { get; }
}

/// <summary>
/// Tracks the set of top-level windows on the system. Shaped after the reference implementation's
/// <c>IWorkspace</c>, trimmed to
/// what WT-1 (window-tracking) requires: enumerate at startup, then track create/destroy/
/// move/resize via events, with <see cref="Poll"/> as the reconciliation fallback for a missed
/// event.
/// </summary>
public interface IWorkspace : IDisposable
{
    /// <summary>A new window became trackable.</summary>
    event EventHandler<WindowEventArgs>? WindowAdded;

    /// <summary>A previously tracked window is gone (destroyed or no longer trackable).</summary>
    event EventHandler<WindowEventArgs>? WindowRemoved;

    /// <summary>
    /// A tracked window's <see cref="IWindow.Bounds"/> changed. <see
    /// cref="WindowEventArgs.IsUserGesture"/> says whether it was the user's own drag or resize.
    /// </summary>
    event EventHandler<WindowEventArgs>? WindowBoundsChanged;

    /// <summary>Has <see cref="Open"/> been called.</summary>
    bool IsOpen { get; }

    /// <summary>A snapshot of the currently tracked windows.</summary>
    IReadOnlyList<IWindow> Snapshot { get; }

    /// <summary>
    /// Enumerates the currently open top-level windows and attaches the create/destroy/move/
    /// resize event source (<c>SetWinEventHook</c> for the Win32 implementation).
    /// </summary>
    void Open();

    /// <summary>
    /// Runs one reconciliation pass against the live window set, raising
    /// <see cref="WindowAdded"/>/<see cref="WindowRemoved"/> for any drift the event source
    /// missed (WT-1's polling fallback).
    /// </summary>
    void Poll();
}
