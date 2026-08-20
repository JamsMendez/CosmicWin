using CosmicWin.Interop;
using CosmicWin.Interop.Win32;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// WT-2: <see cref="Win32Window.SetPosition"/> must forward the requested real-pixel
/// <see cref="Rectangle"/> to the native source exactly as given — once a process is declared
/// PerMonitorV2 DPI-aware, <c>GetWindowRect</c>/<c>SetWindowPos</c> already operate in real
/// (unscaled) pixels, so Win32Window must never apply its own additional scaling on top of that,
/// regardless of which monitor (or DPI) the window happens to be on.
/// </summary>
public class Win32WindowTests
{
    [Fact]
    public void SetPosition_ForwardsRequestedBoundsUnmodified_ToNativeSource()
    {
        var native = new FakeNativeWindowSource();
        native.SeedExistingWindow(new IntPtr(1), "Notepad", Rectangle.FromSize(0, 0, 100, 100));
        var window = new Win32Window(new IntPtr(1), "Notepad", Rectangle.FromSize(0, 0, 100, 100), native);
        var requested = Rectangle.FromSize(1920, 100, 800, 600); // e.g. placed on a second, higher-DPI monitor

        window.SetPosition(requested);

        Assert.True(native.TryGetWindowInfo(new IntPtr(1), out var info));
        Assert.Equal(requested, info.Bounds);
    }

    [Fact]
    public void SetPosition_UpdatesBounds_OnSuccess()
    {
        var native = new FakeNativeWindowSource();
        native.SeedExistingWindow(new IntPtr(2), "Calculator", Rectangle.FromSize(0, 0, 300, 400));
        var window = new Win32Window(new IntPtr(2), "Calculator", Rectangle.FromSize(0, 0, 300, 400), native);

        window.SetPosition(Rectangle.FromSize(50, 50, 300, 400));

        Assert.Equal(Rectangle.FromSize(50, 50, 300, 400), window.Bounds);
    }

    [Fact]
    public void SetPosition_WhenNativeSourceFails_MarksWindowNonRepositionable_AndDoesNotThrow()
    {
        // Threat matrix: "Cross-process window manipulation" — e.g. SetWindowPos on a
        // higher-integrity/protected process's window fails; the caller must degrade to
        // untileable rather than crash.
        var native = new FakeNativeWindowSource();
        native.SeedExistingWindow(new IntPtr(3), "ProtectedApp", Rectangle.FromSize(0, 0, 200, 200));
        native.FailPositionFor(new IntPtr(3));
        var window = new Win32Window(new IntPtr(3), "ProtectedApp", Rectangle.FromSize(0, 0, 200, 200), native);

        var exception = Record.Exception(() => window.SetPosition(Rectangle.FromSize(500, 500, 200, 200)));

        Assert.Null(exception);
        Assert.False(window.CanReposition);
        Assert.True(window.IsAlive);
        Assert.Equal(Rectangle.FromSize(0, 0, 200, 200), window.Bounds); // failed move never applied
    }

    [Fact]
    public void SetPosition_AfterFailure_DoesNotRetryNativeCall_OnSubsequentAttempts()
    {
        var native = new FakeNativeWindowSource();
        native.SeedExistingWindow(new IntPtr(4), "ProtectedApp", Rectangle.FromSize(0, 0, 200, 200));
        native.FailPositionFor(new IntPtr(4));
        var window = new Win32Window(new IntPtr(4), "ProtectedApp", Rectangle.FromSize(0, 0, 200, 200), native);

        window.SetPosition(Rectangle.FromSize(500, 500, 200, 200));
        Assert.Equal(1, native.SetPositionAttemptCount(new IntPtr(4)));

        window.SetPosition(Rectangle.FromSize(600, 600, 200, 200));

        Assert.Equal(1, native.SetPositionAttemptCount(new IntPtr(4))); // no retry loop
    }

    [Fact]
    public void TryActivate_ForwardsToNativeSource_AndReturnsTrue_OnSuccess()
    {
        var native = new FakeNativeWindowSource();
        native.SeedExistingWindow(new IntPtr(5), "Notepad", Rectangle.FromSize(0, 0, 100, 100));
        var window = new Win32Window(new IntPtr(5), "Notepad", Rectangle.FromSize(0, 0, 100, 100), native);

        Assert.True(window.TryActivate());
        Assert.Equal(1, native.ActivationAttemptCount(new IntPtr(5)));
    }

    [Fact]
    public void TryActivate_WhenNativeSourceFails_ReturnsFalse_WithoutThrowing()
    {
        var native = new FakeNativeWindowSource();
        native.SeedExistingWindow(new IntPtr(6), "ProtectedApp", Rectangle.FromSize(0, 0, 200, 200));
        native.FailActivationFor(new IntPtr(6));
        var window = new Win32Window(new IntPtr(6), "ProtectedApp", Rectangle.FromSize(0, 0, 200, 200), native);

        bool activated = true;
        var exception = Record.Exception(() => activated = window.TryActivate());

        Assert.Null(exception);
        Assert.False(activated);
    }
}
