using CosmicWin.Interop;
using CosmicWin.Interop.Tests.TestDoubles;

namespace CosmicWin.Interop.Tests.Contracts;

public class IDisplayManagerContractTests
{
    [Fact]
    public void Primary_IsAlwaysContainedInDisplays()
    {
        var primary = new FakeDisplay(new IntPtr(1), Rectangle.FromSize(0, 0, 1920, 1080), Rectangle.FromSize(0, 0, 1920, 1040), 1.0, isPrimary: true);
        var secondary = new FakeDisplay(new IntPtr(2), Rectangle.FromSize(1920, 0, 1280, 1024), Rectangle.FromSize(1920, 0, 1280, 1024), 1.0, isPrimary: false);
        var manager = new FakeDisplayManager(primary, secondary);

        Assert.Contains(manager.Primary, manager.Displays);
    }

    [Fact]
    public void Displays_ReturnsAllRegisteredDisplays()
    {
        var d1 = new FakeDisplay(new IntPtr(1), Rectangle.Empty, Rectangle.Empty, 1.0, true);
        var d2 = new FakeDisplay(new IntPtr(2), Rectangle.Empty, Rectangle.Empty, 1.0, false);
        var manager = new FakeDisplayManager(d1, d2);

        Assert.Equal(2, manager.Displays.Count);
    }
}
