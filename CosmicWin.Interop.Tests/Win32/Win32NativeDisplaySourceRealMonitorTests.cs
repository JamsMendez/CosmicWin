using CosmicWin.Interop.Win32;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// Task 0.10: first real exercise of the CsWin32-backed <see cref="Win32NativeDisplaySource"/>
/// (and <see cref="Win32DisplayManager"/> wired to it) against the machine's actual monitor
/// configuration — everything before this batch only exercised WT-2's DPI/aggregation logic via
/// <see cref="FakeNativeDisplaySource"/>. Tagged <c>RequiresDesktop</c>, see
/// <see cref="Win32NativeWindowSourceRealWindowTests"/> for the filter commands.
/// </summary>
[Trait("Category", "RequiresDesktop")]
[Collection(RealDesktopCollection.Name)]
public sealed class Win32NativeDisplaySourceRealMonitorTests
{
    [Fact]
    public void EnumerateDisplays_ReturnsAtLeastOnePrimaryDisplay_WithPositiveScalingAndBounds()
    {
        var source = new Win32NativeDisplaySource();

        var displays = source.EnumerateDisplays();

        Assert.NotEmpty(displays);
        Assert.Contains(displays, d => d.IsPrimary);
        Assert.All(displays, d => Assert.True(d.Scaling > 0));
        Assert.All(displays, d => Assert.True(d.Bounds.Width > 0 && d.Bounds.Height > 0));
    }

    [Fact]
    public void Win32DisplayManager_WiredToTheRealSource_ExposesARealPrimaryDisplay()
    {
        var manager = new Win32DisplayManager(new Win32NativeDisplaySource());

        Assert.NotEmpty(manager.Displays);
        Assert.Contains(manager.Primary, manager.Displays);
        Assert.True(manager.Primary.Scaling > 0);
    }
}
