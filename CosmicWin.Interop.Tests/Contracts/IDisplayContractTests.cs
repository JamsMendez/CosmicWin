using CosmicWin.Interop;
using CosmicWin.Interop.Tests.TestDoubles;

namespace CosmicWin.Interop.Tests.Contracts;

public class IDisplayContractTests
{
    [Fact]
    public void WorkArea_IsContainedWithinBounds()
    {
        var display = new FakeDisplay(
            new IntPtr(1),
            bounds: Rectangle.FromSize(0, 0, 1920, 1080),
            workArea: Rectangle.FromSize(0, 0, 1920, 1040),
            scaling: 1.0,
            isPrimary: true);

        Assert.True(display.Bounds.Contains(display.WorkArea));
    }

    [Fact]
    public void Equals_ComparesByHandle_NotByGeometryOrScaling()
    {
        IDisplay a = new FakeDisplay(new IntPtr(9), Rectangle.FromSize(0, 0, 1920, 1080), Rectangle.FromSize(0, 0, 1920, 1080), 1.0, true);
        IDisplay b = new FakeDisplay(new IntPtr(9), Rectangle.FromSize(0, 0, 2560, 1440), Rectangle.FromSize(0, 0, 2560, 1400), 1.5, false);

        Assert.True(a.Equals(b));
    }
}
