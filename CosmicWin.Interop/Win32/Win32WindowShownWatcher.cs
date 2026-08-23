namespace CosmicWin.Interop.Win32;

/// <summary>
/// The real <see cref="IWindowShownWatcher"/>: every top-level window as it is shown, ungated.
/// </summary>
/// <remarks>
/// Reports and decides nothing. It reads the window's style bits and hands them on intact, because
/// this assembly has no idea what a dialog is -- that judgement belongs to the layer that owns
/// <c>WindowFilters</c>, and keeping it there is what lets the predicate be unit-tested without a
/// desktop.
/// </remarks>
public sealed class Win32WindowShownWatcher : IWindowShownWatcher
{
    private readonly INativeWindowSource _source;
    private IDisposable? _subscription;

    public Win32WindowShownWatcher()
        : this(new Win32NativeWindowSource())
    {
    }

    internal Win32WindowShownWatcher(INativeWindowSource source)
    {
        _source = source;
    }

    public event EventHandler<WindowEventArgs>? WindowShown;

    public void Open() => _subscription ??= _source.SubscribeShownWindows(OnShown);

    private void OnShown(nint hwnd)
    {
        // The event carries a handle, not a window, and a window can die between being shown and
        // being asked about. A failed read is simply nothing to report.
        if (!_source.TryGetWindowInfo(hwnd, out var info))
        {
            return;
        }

        WindowShown?.Invoke(this, new WindowEventArgs(new Win32Window(
            hwnd, info.Title, info.Bounds, _source,
            info.ClassName, info.ProcessName, info.Style, info.ExStyle, info.IsOwned)));
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
        WindowShown = null;
    }
}
