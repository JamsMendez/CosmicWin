using CosmicWin.Interop;
using CosmicWin.Interop.Win32;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// WT-2: DPI-correct display geometry resolution. <see cref="Win32DisplayManager"/> must expose
/// each monitor's real (PerMonitorV2) scaling factor and unscaled pixel bounds/work area exactly
/// as reported by the native source, and must resolve the correct primary display.
/// </summary>
public class Win32DisplayManagerTests
{
    [Fact]
    public void Displays_ExposesEachMonitorsScalingFactor_Unmodified()
    {
        var native = new FakeNativeDisplaySource()
            .AddDisplay(new IntPtr(1), Rectangle.FromSize(0, 0, 1920, 1080), Rectangle.FromSize(0, 0, 1920, 1040), scaling: 1.0, isPrimary: true)
            .AddDisplay(new IntPtr(2), Rectangle.FromSize(1920, 0, 2560, 1440), Rectangle.FromSize(1920, 0, 2560, 1440), scaling: 1.5, isPrimary: false);
        var manager = new Win32DisplayManager(native);

        Assert.Equal(1.0, manager.Displays.Single(d => d.Handle == new IntPtr(1)).Scaling);
        Assert.Equal(1.5, manager.Displays.Single(d => d.Handle == new IntPtr(2)).Scaling);
    }

    [Fact]
    public void Displays_ExposesBoundsAndWorkArea_AsRealUnscaledPixelRectangles()
    {
        var bounds = Rectangle.FromSize(0, 0, 3840, 2160);
        var workArea = Rectangle.FromSize(0, 0, 3840, 2120);
        var native = new FakeNativeDisplaySource().AddDisplay(new IntPtr(1), bounds, workArea, scaling: 2.0, isPrimary: true);
        var manager = new Win32DisplayManager(native);

        var display = manager.Displays.Single();
        Assert.Equal(bounds, display.Bounds);
        Assert.Equal(workArea, display.WorkArea);
    }

    [Fact]
    public void Primary_ResolvesToThePrimaryFlaggedDisplay()
    {
        var native = new FakeNativeDisplaySource()
            .AddDisplay(new IntPtr(1), Rectangle.FromSize(0, 0, 1920, 1080), Rectangle.FromSize(0, 0, 1920, 1080), 1.0, isPrimary: false)
            .AddDisplay(new IntPtr(2), Rectangle.FromSize(-1920, 0, 1920, 1080), Rectangle.FromSize(-1920, 0, 1920, 1080), 1.0, isPrimary: true);
        var manager = new Win32DisplayManager(native);

        Assert.Equal(new IntPtr(2), manager.Primary.Handle);
        Assert.Contains(manager.Primary, manager.Displays);
    }

    [Fact]
    public void Constructor_WithNoDisplays_Throws()
    {
        var native = new FakeNativeDisplaySource();

        Assert.Throws<InvalidOperationException>(() => new Win32DisplayManager(native));
    }
}
