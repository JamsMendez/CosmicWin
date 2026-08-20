using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;
using CosmicWin.Layout;

namespace CosmicWin.App.Tests;

/// <summary>
/// Verify-report #21 CRITICAL C1: pure conversion from a display's Interop <see
/// cref="Rectangle"/> work area into a Layout <see cref="Rect"/>. Triangulated with two different
/// origins/sizes to force real field mapping instead of a hardcoded return.
/// </summary>
public sealed class WorkAreaResolverTests
{
    [Fact]
    public void Resolve_PrimaryAtOrigin_ReturnsMatchingRect()
    {
        var display = new FakeDisplay(
            new IntPtr(1), Rectangle.FromSize(0, 0, 1920, 1080), Rectangle.FromSize(0, 0, 1920, 1040), 1.0, isPrimary: true);

        var result = WorkAreaResolver.Resolve(display);

        Assert.Equal(new Rect(0, 0, 1920, 1040), result);
    }

    [Fact]
    public void Resolve_PrimaryOffsetFromOrigin_ReturnsMatchingRect()
    {
        var display = new FakeDisplay(
            new IntPtr(2), Rectangle.FromSize(1920, 0, 1280, 720), Rectangle.FromSize(1920, 40, 1280, 680), 1.0, isPrimary: true);

        var result = WorkAreaResolver.Resolve(display);

        Assert.Equal(new Rect(1920, 40, 1280, 680), result);
    }
}
