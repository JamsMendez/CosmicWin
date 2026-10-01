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
    private const uint MaximizeBox = 0x00010000u;
    private const uint Maximized = 0x01000000u;
    private const uint SysMenuAndMinimizeBox = 0x00080000u | 0x00020000u;

    private static (FakeNativeWindowSource Native, Win32Window Window) WindowWithStyle(int handle, uint style)
    {
        var native = new FakeNativeWindowSource();
        var bounds = Rectangle.FromSize(0, 0, 400, 300);
        native.SeedExistingWindow(new IntPtr(handle), "App", bounds, style: style);
        return (native, new Win32Window(new IntPtr(handle), "App", bounds, native, style: style));
    }

    [Fact]
    public void TrySetMaximizeBox_Clearing_ForwardsToNative_AndDropsTheBitFromStyle()
    {
        var (native, window) = WindowWithStyle(20, SysMenuAndMinimizeBox | MaximizeBox);

        var changed = window.TrySetMaximizeBox(false);

        Assert.Equal(StyleWriteOutcome.Applied, changed);
        Assert.Equal((new IntPtr(20), false), Assert.Single(native.MaximizeBoxCalls));
        Assert.Equal(0u, window.Style & MaximizeBox);
        Assert.Equal(SysMenuAndMinimizeBox, window.Style);
    }

    [Fact]
    public void TrySetMaximizeBox_Setting_ForwardsToNative_AndRaisesTheBitInStyle()
    {
        var (native, window) = WindowWithStyle(21, SysMenuAndMinimizeBox);

        var changed = window.TrySetMaximizeBox(true);

        Assert.Equal(StyleWriteOutcome.Applied, changed);
        Assert.Equal((new IntPtr(21), true), Assert.Single(native.MaximizeBoxCalls));
        Assert.Equal(MaximizeBox, window.Style & MaximizeBox);
    }

    [Fact]
    public void TrySetMaximizeBox_WhenNativeRefuses_ReturnsFalse_KeepsStyle_AndStaysRepositionable()
    {
        // Threat matrix, same row as SetPosition: an elevated window refuses a style write from a
        // non-elevated caller. Unlike a refused reposition it must NOT make the window untileable.
        var (native, window) = WindowWithStyle(22, SysMenuAndMinimizeBox | MaximizeBox);
        native.FailStyleChangesFor(new IntPtr(22));

        var exception = Record.Exception(() => Assert.Equal(StyleWriteOutcome.Refused, window.TrySetMaximizeBox(false)));

        Assert.Null(exception);
        Assert.Equal(MaximizeBox, window.Style & MaximizeBox);
        Assert.True(window.CanReposition);
    }

    [Fact]
    public void TrySetMaximizeBox_WhenNativeTimesOut_ReportsTimedOut_AndLeavesTheCachedStyleAlone()
    {
        var (native, window) = WindowWithStyle(25, SysMenuAndMinimizeBox | MaximizeBox);
        native.TimeOutStyleChangesFor(new IntPtr(25));

        var outcome = window.TrySetMaximizeBox(false);

        // Unknown, not refused: the cached style must not claim either state.
        Assert.Equal(StyleWriteOutcome.TimedOut, outcome);
        Assert.Equal(MaximizeBox, window.Style & MaximizeBox);
    }

    [Fact]
    public void TrySetMaximizeBox_OnADeadWindow_ReturnsFalse_WithoutAskingTheOs()
    {
        var (native, window) = WindowWithStyle(23, SysMenuAndMinimizeBox | MaximizeBox);
        window.MarkDead();

        Assert.Equal(StyleWriteOutcome.Refused, window.TrySetMaximizeBox(false));
        Assert.Empty(native.MaximizeBoxCalls);
    }

    [Fact]
    public void TryRestore_ForwardsToNative_AndClearsTheMaximizedBit()
    {
        var (native, window) = WindowWithStyle(24, SysMenuAndMinimizeBox | MaximizeBox | Maximized);

        var restored = window.TryRestore();

        Assert.True(restored);
        Assert.Equal(new IntPtr(24), Assert.Single(native.RestoreAsks));
        Assert.Equal(0u, window.Style & Maximized);
    }

    [Fact]
    public void TryRestore_WhenNativeRefuses_ReturnsFalse_AndKeepsStyle()
    {
        var (native, window) = WindowWithStyle(25, SysMenuAndMinimizeBox | Maximized);
        native.FailStyleChangesFor(new IntPtr(25));

        var exception = Record.Exception(() => Assert.False(window.TryRestore()));

        Assert.Null(exception);
        Assert.Equal(Maximized, window.Style & Maximized);
    }

    [Fact]
    public void TryRestore_OnADeadWindow_ReturnsFalse_WithoutAskingTheOs()
    {
        var (native, window) = WindowWithStyle(26, SysMenuAndMinimizeBox | Maximized);
        window.MarkDead();

        Assert.False(window.TryRestore());
        Assert.Empty(native.RestoreAsks);
    }

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

    /// <summary>
    /// <see cref="Win32Window.Activate"/> hands the native source's outcome UP unflattened.
    /// </summary>
    /// <remarks>
    /// This class sat directly on top of an escalation that reports six distinct endings and
    /// forwarded exactly one bit of it. Everything above -- the executor, the desktop trace, the
    /// person reading the log afterwards -- inherited that loss, and no amount of instrumenting
    /// further up could recover what was discarded here.
    /// </remarks>
    [Theory]
    [InlineData(ActivationOutcome.AlreadyForeground)]
    [InlineData(ActivationOutcome.Direct)]
    [InlineData(ActivationOutcome.AttachedInput)]
    [InlineData(ActivationOutcome.InputUnlocked)]
    [InlineData(ActivationOutcome.Failed)]
    [InlineData(ActivationOutcome.TimedOut)]
    public void Activate_ForwardsTheNativeSourcesOutcome_Unflattened(ActivationOutcome outcome)
    {
        var native = new FakeNativeWindowSource();
        native.SeedExistingWindow(new IntPtr(7), "Notepad", Rectangle.FromSize(0, 0, 100, 100));
        native.ActivateAs(new IntPtr(7), outcome);
        var window = new Win32Window(new IntPtr(7), "Notepad", Rectangle.FromSize(0, 0, 100, 100), native);

        Assert.Equal(outcome, window.Activate());
    }

    /// <summary>A dead window is never activated, and never asks the OS about it either.</summary>
    [Fact]
    public void Activate_OnADeadWindow_RefusesWithoutTouchingTheNativeSource()
    {
        var native = new FakeNativeWindowSource();
        native.SeedExistingWindow(new IntPtr(8), "Gone", Rectangle.FromSize(0, 0, 100, 100));
        var window = new Win32Window(new IntPtr(8), "Gone", Rectangle.FromSize(0, 0, 100, 100), native);
        window.MarkDead();

        Assert.Equal(ActivationOutcome.Failed, window.Activate());
        Assert.Equal(0, native.ActivationAttemptCount(new IntPtr(8)));
    }

    // 5 pass-through facts pinning IWindow's new raw-descriptor surface (ClassName,
    // ProcessName, Style, ExStyle, IsOwned), added so an App-layer adapter can build a Layout
    // WindowDescriptor from an IWindow without CosmicWin.Layout ever referencing Win32 (design
    // D1/D8/D7). Confirmed RED: named args `className:`/`processName:`/`style:`/`exStyle:`/
    // `isOwned:` do not exist on Win32Window's constructor (no such parameter, compile error) and
    // the properties do not exist on IWindow (CS1061) before 's GREEN.

    [Fact]
    public void ClassName_ReflectsConstructorValue()
    {
        var native = new FakeNativeWindowSource();
        var window = new Win32Window(
            new IntPtr(7), "Notepad", Rectangle.FromSize(0, 0, 100, 100), native, className: "Notepad");

        Assert.Equal("Notepad", window.ClassName);
    }

    [Fact]
    public void ProcessName_ReflectsConstructorValue()
    {
        var native = new FakeNativeWindowSource();
        var window = new Win32Window(
            new IntPtr(8), "Notepad", Rectangle.FromSize(0, 0, 100, 100), native, processName: "notepad.exe");

        Assert.Equal("notepad.exe", window.ProcessName);
    }

    [Fact]
    public void Style_ReflectsConstructorValue()
    {
        var native = new FakeNativeWindowSource();
        var window = new Win32Window(
            new IntPtr(9), "Notepad", Rectangle.FromSize(0, 0, 100, 100), native, style: 0x00080000u);

        Assert.Equal(0x00080000u, window.Style);
    }

    [Fact]
    public void ExStyle_ReflectsConstructorValue()
    {
        var native = new FakeNativeWindowSource();
        var window = new Win32Window(
            new IntPtr(10), "Notepad", Rectangle.FromSize(0, 0, 100, 100), native, exStyle: 0x00000080u);

        Assert.Equal(0x00000080u, window.ExStyle);
    }

    [Fact]
    public void IsOwned_ReflectsConstructorValue()
    {
        var native = new FakeNativeWindowSource();
        var window = new Win32Window(
            new IntPtr(11), "Notepad", Rectangle.FromSize(0, 0, 100, 100), native, isOwned: true);

        Assert.True(window.IsOwned);
    }
}
