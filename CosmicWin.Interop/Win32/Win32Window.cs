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
    /// <summary>Real WS_SYSMENU|WS_MAXIMIZEBOX|WS_MINIMIZEBOX bits -- regression-safety default so pre-existing positional call sites read as tileable, not auto-excluded.</summary>
    private const uint TileableStyleDefault = 0x00080000u | 0x00010000u | 0x00020000u;

    private readonly INativeWindowSource _nativeSource;
    private string _title;
    private Rectangle _bounds;

    public Win32Window(
        nint handle,
        string title,
        Rectangle bounds,
        INativeWindowSource nativeSource,
        string className = "",
        string processName = "",
        uint style = TileableStyleDefault,
        uint exStyle = 0u,
        bool isOwned = false)
    {
        Handle = handle;
        _title = title;
        _bounds = bounds;
        _nativeSource = nativeSource;
        IsAlive = true;
        ClassName = className;
        ProcessName = processName;
        Style = style;
        ExStyle = exStyle;
        IsOwned = isOwned;
    }

    public nint Handle { get; }

    public string Title => IsAlive ? _title : string.Empty;

    public Rectangle Bounds => IsAlive ? _bounds : Rectangle.Empty;

    public bool IsAlive { get; private set; }

    public bool CanReposition { get; private set; } = true;

    public string ClassName { get; private set; }

    public string ProcessName { get; private set; }

    public uint Style { get; private set; }

    public uint ExStyle { get; private set; }

    public bool IsOwned { get; private set; }

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

    /// <summary>
    /// Forwards the native source's outcome UP unflattened.
    /// </summary>
    /// <remarks>
    /// A dead window reports <see cref="ActivationOutcome.Failed"/> WITHOUT asking the OS: there is
    /// no window left to give the foreground to, so every rung would be refused anyway, and calling
    /// is the one thing that could produce a surprise. It is the honest answer of the six —
    /// <see cref="ActivationOutcome.TimedOut"/> would claim a worker ran, and every success value
    /// would be a lie.
    /// </remarks>
    public ActivationOutcome Activate() =>
        IsAlive ? _nativeSource.Activate(Handle) : ActivationOutcome.Failed;

    public bool TryActivate() => Activate().Confirmed();

    public bool TryClose() => IsAlive && _nativeSource.TryClose(Handle);

    internal void Refresh(string title, Rectangle bounds, string className, string processName, uint style, uint exStyle, bool isOwned)
    {
        _title = title;
        _bounds = bounds;
        ClassName = className;
        ProcessName = processName;
        Style = style;
        ExStyle = exStyle;
        IsOwned = isOwned;
    }

    internal void MarkDead() => IsAlive = false;

    public bool Equals(IWindow? other) => other is not null && Handle == other.Handle;

    public override bool Equals(object? obj) => obj is IWindow other && Equals(other);

    public override int GetHashCode() => Handle.GetHashCode();
}
