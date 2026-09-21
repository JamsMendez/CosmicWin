namespace CosmicWin.Interop.Win32;

/// <summary>
/// <see cref="IDisplay"/> backed by a <see cref="NativeDisplayInfo"/> read at
/// <see cref="Win32DisplayManager"/> construction time and re-read by
/// <see cref="Win32DisplayManager.Refresh"/>.
/// </summary>
/// <remarks>
/// <para>
/// The OBJECT outlives its readings on purpose. The tree manager and the
/// workspace adapter keep the <see cref="IDisplay"/> they were handed at startup and read
/// <see cref="WorkArea"/> from it every time they lay a window out, so a taskbar that moves, hides
/// or is resized has to show up through the instance they already hold. Handing out a fresh object
/// per reading would leave every one of them on the geometry of the moment the process started.
/// </para>
/// <para>
/// One immutable snapshot behind one reference, rather than four separately-assigned properties:
/// <see cref="Refresh"/> runs on the UI thread while a chord is answered on a pool thread, and a
/// <see cref="Rectangle"/> is sixteen bytes -- a plain field write can be observed half-done. A
/// reference swap cannot, and it keeps Bounds, WorkArea and Scaling mutually consistent.
/// </para>
/// </remarks>
internal sealed class Win32Display : IDisplay
{
    private sealed record State(Rectangle Bounds, Rectangle WorkArea, double Scaling, bool IsPrimary);

    private volatile State _state;

    public Win32Display(NativeDisplayInfo info)
    {
        Handle = info.Handle;
        _state = StateOf(info);
    }

    public nint Handle { get; }

    public Rectangle Bounds => _state.Bounds;

    public Rectangle WorkArea => _state.WorkArea;

    public double Scaling => _state.Scaling;

    public bool IsPrimary => _state.IsPrimary;

    /// <summary>Adopts a fresh reading; true when anything a layout depends on actually differs.</summary>
    internal bool Refresh(NativeDisplayInfo info)
    {
        var next = StateOf(info);
        if (next == _state)
        {
            return false;
        }

        _state = next;
        return true;
    }

    private static State StateOf(NativeDisplayInfo info) =>
        new(info.Bounds, info.WorkArea, info.Scaling, info.IsPrimary);

    public bool Equals(IDisplay? other) => other is not null && Handle == other.Handle;

    public override bool Equals(object? obj) => obj is IDisplay other && Equals(other);

    public override int GetHashCode() => Handle.GetHashCode();
}
