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

    // The taskbar moving, hiding or being resized changes ONLY the work area: the monitor's bounds
    // stay put. Refresh is how the process finds out, since nothing else re-reads the OS.
    [Fact]
    public void Refresh_WhenTheTaskbarChangesTheWorkArea_ReturnsThatDisplay()
    {
        var native = new FakeNativeDisplaySource()
            .AddDisplay(new IntPtr(1), Rectangle.FromSize(0, 0, 1920, 1080), Rectangle.FromSize(0, 0, 1920, 1040), 1.0, isPrimary: true);
        var manager = new Win32DisplayManager(native);

        native.Change(new IntPtr(1), d => d with { WorkArea = Rectangle.FromSize(0, 0, 1920, 1080) });
        var changed = manager.Refresh();

        Assert.Equal(new IntPtr(1), Assert.Single(changed).Handle);
    }

    // Identity is the design: TreeManager and the workspace adapter both hold the IDisplay they were
    // handed at startup, so a fresh object would leave every one of them reading the old area.
    [Fact]
    public void Refresh_UpdatesTheSameDisplayObjectInPlace()
    {
        var native = new FakeNativeDisplaySource()
            .AddDisplay(new IntPtr(1), Rectangle.FromSize(0, 0, 1920, 1080), Rectangle.FromSize(0, 0, 1920, 1040), 1.0, isPrimary: true);
        var manager = new Win32DisplayManager(native);
        var before = manager.Displays.Single();

        native.Change(new IntPtr(1), d => d with { WorkArea = Rectangle.FromSize(0, 0, 1920, 1080) });
        manager.Refresh();

        Assert.Same(before, manager.Displays.Single());
        Assert.Equal(Rectangle.FromSize(0, 0, 1920, 1080), before.WorkArea);
    }

    [Fact]
    public void Refresh_WhenNothingChanged_ReturnsNothing()
    {
        var native = new FakeNativeDisplaySource()
            .AddDisplay(new IntPtr(1), Rectangle.FromSize(0, 0, 1920, 1080), Rectangle.FromSize(0, 0, 1920, 1040), 1.0, isPrimary: true);
        var manager = new Win32DisplayManager(native);

        Assert.Empty(manager.Refresh());
    }

    [Fact]
    public void Refresh_ReturnsOnlyTheDisplaysThatChanged()
    {
        var native = new FakeNativeDisplaySource()
            .AddDisplay(new IntPtr(1), Rectangle.FromSize(0, 0, 1920, 1080), Rectangle.FromSize(0, 0, 1920, 1040), 1.0, isPrimary: true)
            .AddDisplay(new IntPtr(2), Rectangle.FromSize(1920, 0, 1280, 720), Rectangle.FromSize(1920, 0, 1280, 720), 1.0, isPrimary: false);
        var manager = new Win32DisplayManager(native);

        native.Change(new IntPtr(2), d => d with { WorkArea = Rectangle.FromSize(1920, 0, 1280, 680) });

        Assert.Equal(new IntPtr(2), Assert.Single(manager.Refresh()).Handle);
    }

    // Bounds and DPI move with a resolution or scale change, and a tile computed from the old value
    // is just as wrong as one computed from the old taskbar.
    [Fact]
    public void Refresh_WhenTheScalingChanges_ReturnsThatDisplayToo()
    {
        var native = new FakeNativeDisplaySource()
            .AddDisplay(new IntPtr(1), Rectangle.FromSize(0, 0, 1920, 1080), Rectangle.FromSize(0, 0, 1920, 1040), 1.0, isPrimary: true);
        var manager = new Win32DisplayManager(native);

        native.Change(new IntPtr(1), d => d with { Scaling = 1.5 });

        Assert.Equal(1.5, Assert.Single(manager.Refresh()).Scaling);
    }

    // Once the change has been reported it is the new normal: the next tick must not report it again,
    // or every tick would reflow the layout.
    [Fact]
    public void Refresh_ReportsAChangeOnce()
    {
        var native = new FakeNativeDisplaySource()
            .AddDisplay(new IntPtr(1), Rectangle.FromSize(0, 0, 1920, 1080), Rectangle.FromSize(0, 0, 1920, 1040), 1.0, isPrimary: true);
        var manager = new Win32DisplayManager(native);

        native.Change(new IntPtr(1), d => d with { WorkArea = Rectangle.FromSize(0, 0, 1920, 1080) });
        manager.Refresh();

        Assert.Empty(manager.Refresh());
    }

    // Unplugging is a different event with its own handling (TreeManager.OnDisplayDisconnected); a
    // work-area poll that throws or forgets the display when the enumeration comes back short would
    // turn every hot-unplug into a crash on the tick.
    [Fact]
    public void Refresh_WhenAMonitorIsNoLongerReported_KeepsWhatItLastKnewAndDoesNotThrow()
    {
        var native = new FakeNativeDisplaySource()
            .AddDisplay(new IntPtr(1), Rectangle.FromSize(0, 0, 1920, 1080), Rectangle.FromSize(0, 0, 1920, 1040), 1.0, isPrimary: true)
            .AddDisplay(new IntPtr(2), Rectangle.FromSize(1920, 0, 1280, 720), Rectangle.FromSize(1920, 0, 1280, 720), 1.0, isPrimary: false);
        var manager = new Win32DisplayManager(native);

        native.Remove(new IntPtr(2));

        Assert.Empty(manager.Refresh());
        Assert.Equal(2, manager.Displays.Count);
    }
}
