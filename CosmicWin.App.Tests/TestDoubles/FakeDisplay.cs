using CosmicWin.Interop;

namespace CosmicWin.App.Tests.TestDoubles;

/// <summary>
/// Minimal in-memory <see cref="IDisplay"/> used to prove <see cref="WorkAreaResolver"/> derives
/// a real work area with no live desktop. Mirrors the Interop.Tests <c>FakeDisplay</c> shape
/// without depending on that internal type across assemblies.
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
