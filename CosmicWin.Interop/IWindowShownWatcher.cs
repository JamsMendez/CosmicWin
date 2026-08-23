namespace CosmicWin.Interop;

/// <summary>
/// Reports every top-level window as it is SHOWN, with no trackability filtering at all.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately separate from <see cref="IWorkspace"/>, and deliberately wider. A workspace reports
/// the windows worth TILING, which is why <c>Win32NativeWindowSource.IsTrackable</c> drops every
/// owned window -- and a modal dialog always has an owner, so a workspace has never seen one and
/// still does not. Widening that gate would push every tooltip, dropdown, context menu and IME
/// candidate list through the tiling pipeline, which is the one function keeping them out.
/// </para>
/// <para>
/// So this watches from beside it rather than through it. It reports everything and decides
/// nothing: what a shown window MEANS is a question for the layer that knows about dialogs, and
/// this assembly stays Win32-shaped.
/// </para>
/// </remarks>
public interface IWindowShownWatcher : IDisposable
{
    /// <summary>A top-level window was shown. Raised on the thread the underlying hook uses.</summary>
    event EventHandler<WindowEventArgs>? WindowShown;

    /// <summary>Attaches the event source. Nothing is raised until this is called.</summary>
    void Open();
}
