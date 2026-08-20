namespace CosmicWin.Interop;

/// <summary>
/// Carries the <see cref="IWindow"/> a workspace event refers to. Mirrors winman's
/// <c>WindowChangedEventArgs</c> shape (<c>fancywm/winman/src/WinMan/Events.cs</c>).
/// </summary>
public sealed class WindowEventArgs : EventArgs
{
    public WindowEventArgs(IWindow window)
    {
        Window = window;
    }

    public IWindow Window { get; }
}

/// <summary>
/// Tracks the set of top-level windows on the system. Mirrors the shape of winman's
/// <c>WinMan.IWorkspace</c> (see <c>fancywm/winman/src/WinMan/IWorkspace.cs</c>), trimmed to
/// what WT-1 (window-tracking) requires: enumerate at startup, then track create/destroy/
/// move/resize via events, with <see cref="Poll"/> as the reconciliation fallback for a missed
/// event (spec scenario "Hook misses an event").
/// </summary>
public interface IWorkspace : IDisposable
{
    /// <summary>A new window became trackable.</summary>
    event EventHandler<WindowEventArgs>? WindowAdded;

    /// <summary>A previously tracked window is gone (destroyed or no longer trackable).</summary>
    event EventHandler<WindowEventArgs>? WindowRemoved;

    /// <summary>A tracked window's <see cref="IWindow.Bounds"/> changed.</summary>
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
