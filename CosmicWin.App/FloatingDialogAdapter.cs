using CosmicWin.Interop;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App;

/// <summary>
/// Centres a modal dialog on the work area it opens on, and leaves everything else alone.
/// </summary>
/// <remarks>
/// <para>
/// The narrowing that <see cref="IWindowShownWatcher"/> deliberately does not do. That watcher
/// reports EVERY window being shown -- tooltips, dropdowns, context menus, IME candidate lists --
/// because the tiling pipeline's own gate (<c>IsTrackable</c>, which drops anything with an owner)
/// would otherwise hide the dialogs along with them. Which makes
/// <see cref="WindowFilters.IsModalDialog"/> the only thing standing between a context menu and
/// being moved out from under the pointer that opened it.
/// </para>
/// <para>
/// Nothing here touches the layout tree, and nothing here can. A dialog is auto-excluded from
/// tiling by construction -- <see cref="WindowFilters.IsModalDialog"/> only ever matches windows
/// <see cref="WindowFilters.IsAutoExcluded"/> already refuses -- so it has no siblings to divide a
/// region with and leaves nothing to reflow when it closes.
/// </para>
/// </remarks>
public sealed class FloatingDialogAdapter : IDisposable
{
    private readonly IWindowShownWatcher _watcher;
    private readonly TreeManager _treeManager;
    private readonly Func<ExceptionList> _exceptions;
    private readonly Func<bool> _isPaused;

    /// <summary>
    /// Every dialog this adapter has centred, with the rectangle it ARRIVED with.
    /// </summary>
    /// <remarks>
    /// The opened size is the whole reason this is remembered rather than read live. A snap replaces
    /// the dialog's size with half the screen, so by the time the user asks for it back there is
    /// nothing on the window itself to restore it from -- the application laid it out once, for its
    /// own content, and that is the only rectangle worth returning to.
    /// </remarks>
    private readonly Dictionary<nint, (IWindow Window, Rectangle Opened)> _dialogs = new();

    /// <summary>
    /// Records every window the watcher delivers and what was decided about it. Unset in normal
    /// runs -- this is diagnostic volume, one line per owned window shown anywhere on the desktop.
    /// </summary>
    /// <remarks>
    /// Exists because "the dialog was not centred" has at least four indistinguishable causes: the
    /// hook never fired, the read failed, the style bits were not yet what they are a moment later,
    /// or the reposition was refused. The same lesson the desktop trace already taught -- a path
    /// that quietly does nothing reads exactly like a broken feature.
    /// </remarks>
    public Diagnostics.IDesktopTrace? Trace { get; set; }

    public FloatingDialogAdapter(
        IWindowShownWatcher watcher, TreeManager treeManager,
        Func<ExceptionList> exceptions, Func<bool> isPaused)
    {
        _watcher = watcher;
        _treeManager = treeManager;
        _exceptions = exceptions;
        _isPaused = isPaused;

        _watcher.WindowShown += OnWindowShown;
    }

    private void OnWindowShown(object? sender, WindowEventArgs e)
    {
        if (_isPaused())
        {
            return;
        }

        var window = e.Window;

        // A window that has already refused a reposition is never asked again. The contract makes
        // CanReposition one-way, and a dialog shown repeatedly would otherwise be fought once per
        // appearance for as long as the app runs.
        if (!window.IsAlive || !window.CanReposition)
        {
            return;
        }

        var descriptor = WindowDescriptorBuilder.Build(window);
        var isDialog = WindowFilters.IsModalDialog(descriptor);
        Trace?.Record(
            $"shown hwnd=0x{window.Handle:X} modal={isDialog} owned={descriptor.IsOwned} " +
            $"style=0x{descriptor.Style:X8} exstyle=0x{descriptor.ExStyle:X8} " +
            $"class={descriptor.ClassName} proc={descriptor.ProcessName} " +
            $"rect=[L={window.Bounds.Left} T={window.Bounds.Top} " +
            $"W={window.Bounds.Width} H={window.Bounds.Height}] title={descriptor.Title}");

        if (!isDialog)
        {
            return;
        }

        // The user's manual list means "leave this alone", and it has to be read directly here. The
        // automatic filter cannot express it for a dialog: every dialog is auto-excluded already, so
        // asking IsExcluded would answer true for all of them and the setting would silently never
        // apply.
        if (_exceptions().Matches(descriptor))
        {
            return;
        }

        // The display's own work area, not WorkAreaResolver's Rect: a dialog is positioned through
        // IWindow.SetPosition, which speaks Interop's Rectangle, and the tree's Win32-free geometry
        // type has no part in a window that never enters the tree.
        // Recorded BEFORE centring, so it is the size the application chose and not the one this
        // adapter just gave it. Dead entries are swept here rather than on a timer: a dialog closing
        // raises nothing this adapter listens to, and the only moment it is certain to run again is
        // when the next one opens.
        _dialogs.Remove(window.Handle);
        foreach (var stale in _dialogs.Where(entry => !entry.Value.Window.IsAlive).Select(entry => entry.Key).ToArray())
        {
            _dialogs.Remove(stale);
        }

        _dialogs[window.Handle] = (window, window.Bounds);

        var display = _treeManager.ResolveDisplay(window.Bounds);
        var centred = DialogPlacement.Centre(window.Bounds, display.WorkArea);
        window.SetPosition(centred);

        Trace?.Record(
            $"centred hwnd=0x{window.Handle:X} -> [L={centred.Left} T={centred.Top} " +
            $"W={centred.Width} H={centred.Height}] accepted={window.CanReposition} " +
            $"readback=[L={window.Bounds.Left} T={window.Bounds.Top} " +
            $"W={window.Bounds.Width} H={window.Bounds.Height}]");
    }


    /// <summary>
    /// Snaps a floating dialog in a direction, reporting whether this adapter owns that window.
    /// </summary>
    /// <remarks>
    /// <see langword="false"/> is a real answer, not a failure: it tells the caller this handle is
    /// none of its business, so the chord can fall through to its own no-op rather than guessing at
    /// a window nobody is managing.
    /// </remarks>
    public bool TrySnap(nint handle, Direction direction)
    {
        if (_isPaused() || !_dialogs.TryGetValue(handle, out var entry))
        {
            return false;
        }

        var (window, opened) = entry;

        // A closed dialog is forgotten rather than refused each time -- and never resurrected by a
        // chord that arrives after it is gone.
        if (!window.IsAlive || !window.CanReposition)
        {
            _dialogs.Remove(handle);
            return false;
        }

        var display = _treeManager.ResolveDisplay(window.Bounds);
        window.SetPosition(DialogPlacement.Snap(window.Bounds, display.WorkArea, direction, opened));
        return true;
    }

    public void Dispose() => _watcher.WindowShown -= OnWindowShown;
}