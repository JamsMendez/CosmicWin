namespace CosmicWin.Interop.Win32;

/// <summary>
/// <see cref="IWindow"/> backed by <see cref="INativeWindowSource"/>, owned by
/// <see cref="Win32Workspace"/>.
/// </summary>
/// <remarks>
/// WT-2: <see cref="SetPosition"/> forwards the requested real-pixel <see cref="Rectangle"/> to
/// the native source completely unmodified — no additional DPI scaling is ever applied here.
/// Once a process is declared PerMonitorV2-aware, <c>GetWindowRect</c>/<c>SetWindowPos</c>
/// already operate in real pixels; computing DPI-correct target rectangles from a monitor's
/// <see cref="IDisplay.Scaling"/> is the future Layout engine's job, not this class's.
/// </remarks>
internal sealed class Win32Window : IWindow
{
    private readonly INativeWindowSource _nativeSource;
    private string _title;
    private Rectangle _bounds;

    public Win32Window(nint handle, string title, Rectangle bounds, INativeWindowSource nativeSource)
    {
        Handle = handle;
        _title = title;
        _bounds = bounds;
        _nativeSource = nativeSource;
        IsAlive = true;
    }

    public nint Handle { get; }

    public string Title => IsAlive ? _title : string.Empty;

    public Rectangle Bounds => IsAlive ? _bounds : Rectangle.Empty;

    public bool IsAlive { get; private set; }

    public bool CanReposition { get; private set; } = true;

    public void SetPosition(Rectangle bounds)
    {
        if (!IsAlive || !CanReposition)
        {
            return;
        }

        if (!_nativeSource.SetWindowPosition(Handle, bounds))
        {
            // Threat matrix: "Cross-process window manipulation" — a failed SetWindowPos (e.g.
            // target belongs to a higher-integrity/protected process) degrades the window to
            // non-repositionable instead of throwing or being retried in a loop.
            CanReposition = false;
            return;
        }

        _bounds = bounds;
    }

    public bool TryActivate() => IsAlive && _nativeSource.TryActivateWindow(Handle);

    internal void Refresh(string title, Rectangle bounds)
    {
        _title = title;
        _bounds = bounds;
    }

    internal void MarkDead() => IsAlive = false;

    public bool Equals(IWindow? other) => other is not null && Handle == other.Handle;

    public override bool Equals(object? obj) => obj is IWindow other && Equals(other);

    public override int GetHashCode() => Handle.GetHashCode();
}
