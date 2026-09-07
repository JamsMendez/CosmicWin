namespace CosmicWin.Interop;

/// <summary>
/// Carries the <see cref="IWindow"/> a workspace event refers to.
/// </summary>
/// <summary>
/// Whether a window being announced is NEW to the system, or merely new to CosmicWin.
/// </summary>
/// <remarks>
/// <para>
/// The same event carries both, and they are the same fact about a window appearing and a
/// completely different fact about intent. Windows decides where a new window is born, and that
/// decision is worth overruling; a window that has been sitting on another desktop all along made
/// no decision to overrule, and moving it is moving something the user put there.
/// </para>
/// <para>
/// Adoption of another desktop's windows is guaranteed, not exotic: <c>IsTrackable</c> rejects
/// cloaked windows and DWM cloaks every window on a desktop the user is not looking at, so a
/// starting window manager can only ever adopt the desktop in view. Every other desktop is adopted
/// later, the moment the user walks over to it.
/// </para>
/// </remarks>
public enum WindowArrival
{
    /// <summary>The window has just been created. The default, so an unqualified announcement keeps meaning what it always meant.</summary>
    Created = 0,

    /// <summary>The window already existed and is only now being tracked -- a startup sweep, or a reconciliation pass.</summary>
    Adopted = 1,
}

public sealed class WindowEventArgs : EventArgs
{
    public WindowEventArgs(IWindow window, bool isUserGesture = false, WindowArrival arrival = WindowArrival.Created)
    {
        Window = window;
        IsUserGesture = isUserGesture;
        Arrival = arrival;
    }

    public IWindow Window { get; }

    /// <summary>
    /// Whether this window is genuinely new, or was already there. Meaningful only on
    /// <see cref="IWorkspace.WindowAdded"/>; the other events leave it at its default.
    /// </summary>
    public WindowArrival Arrival { get; }

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
/// Carries a rectangle a window is passing THROUGH, rather than one it has settled on.
/// </summary>
/// <remarks>
/// <para>
/// Its own type, and not a flag on <see cref="WindowEventArgs"/>, because the two carry different
/// GOODS. That one hands over a window whose <see cref="IWindow.Bounds"/> are the answer; this one
/// hands over a rectangle the window's own cached bounds deliberately do NOT yet know about.
/// </para>
/// <para>
/// Nothing that lays windows out may listen to this. A drag is one gesture and the layout answers
/// it once, at the drop -- that is what <c>_beingDragged</c> exists to guarantee, and it was bought
/// with a measured defect: a tiled window under the mouse flickering against its own snap-back
/// dozens of times a second. This event exists for the one listener that must keep up with the
/// gesture without joining it -- something DRAWN over the window, which changes nothing about where
/// the window goes.
/// </para>
/// </remarks>
public sealed class WindowBoundsChangingEventArgs(IWindow window, Rectangle bounds) : EventArgs
{
    /// <summary>The window the user is dragging or resizing right now.</summary>
    public IWindow Window { get; } = window;

    /// <summary>
    /// Where it is at this instant, read fresh from the OS.
    /// </summary>
    /// <remarks>
    /// Read from HERE rather than from <see cref="Window"/>, and the indirection is the whole
    /// point: writing this into the window's cache mid-gesture would leave the cached rectangle
    /// already equal to the final one at the drop, so the settled
    /// <see cref="IWorkspace.WindowBoundsChanged"/> -- the only event carrying
    /// <see cref="WindowEventArgs.IsUserGesture"/>, and the only one the tree resizes from -- would
    /// see no change and never fire at all.
    /// </remarks>
    public Rectangle Bounds { get; } = bounds;
}

/// <summary>
/// Tracks the set of top-level windows on the system. Trimmed to
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

    /// <summary>
    /// A tracked window is being dragged or resized BY HAND, right now, and has reached
    /// <see cref="WindowBoundsChangingEventArgs.Bounds"/>. Raised once per frame of the gesture and
    /// never outside one; <see cref="WindowBoundsChanged"/> reports the same gesture exactly once,
    /// when the user lets go.
    /// </summary>
    /// <remarks>
    /// For listeners that DRAW, never for listeners that MOVE -- see
    /// <see cref="WindowBoundsChangingEventArgs"/>.
    /// </remarks>
    event EventHandler<WindowBoundsChangingEventArgs>? WindowBoundsChanging;

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
