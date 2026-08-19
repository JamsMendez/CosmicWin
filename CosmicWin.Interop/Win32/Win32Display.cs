namespace CosmicWin.Interop.Win32;

/// <summary>
/// <see cref="IDisplay"/> backed by a <see cref="NativeDisplayInfo"/> snapshot read at
/// <see cref="Win32DisplayManager"/> construction time.
/// </summary>
internal sealed class Win32Display : IDisplay
{
    public Win32Display(NativeDisplayInfo info)
    {
        Handle = info.Handle;
        Bounds = info.Bounds;
        WorkArea = info.WorkArea;
        Scaling = info.Scaling;
        IsPrimary = info.IsPrimary;
    }

    public nint Handle { get; }

    public Rectangle Bounds { get; }

    public Rectangle WorkArea { get; }

    public double Scaling { get; }

    public bool IsPrimary { get; }

    public bool Equals(IDisplay? other) => other is not null && Handle == other.Handle;

    public override bool Equals(object? obj) => obj is IDisplay other && Equals(other);

    public override int GetHashCode() => Handle.GetHashCode();
}
