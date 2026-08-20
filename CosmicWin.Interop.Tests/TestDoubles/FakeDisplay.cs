using CosmicWin.Interop;

namespace CosmicWin.Interop.Tests.TestDoubles;

/// <summary>
/// Hand-rolled in-memory <see cref="IDisplay"/> used to pin down the contract before the
/// Win32-backed implementation exists.
/// </summary>
internal sealed class FakeDisplay : IDisplay
{
    public FakeDisplay(IntPtr handle, Rectangle bounds, Rectangle workArea, double scaling, bool isPrimary)
    {
        Handle = handle;
        Bounds = bounds;
        WorkArea = workArea;
        Scaling = scaling;
        IsPrimary = isPrimary;
    }

    public IntPtr Handle { get; }

    public Rectangle Bounds { get; }

    public Rectangle WorkArea { get; }

    public double Scaling { get; }

    public bool IsPrimary { get; }

    public bool Equals(IDisplay? other) => other is not null && Handle == other.Handle;

    public override bool Equals(object? obj) => obj is IDisplay other && Equals(other);

    public override int GetHashCode() => Handle.GetHashCode();
}
