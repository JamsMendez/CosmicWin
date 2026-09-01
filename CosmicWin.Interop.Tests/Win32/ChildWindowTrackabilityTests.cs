using CosmicWin.Interop.Win32;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// A CHILD window is never trackable, however unowned and uncloaked it looks.
/// </summary>
/// <remarks>
/// <para>
/// Measured on real hardware. Clipchamp publishes two of its own chrome pieces as windows the
/// desktop-wide WinEvent hook reports -- <c>InputNonClientPointerSource</c> (its title bar) and
/// <c>ReunionWindowingCaptionControls</c> (its minimise/maximise/close buttons). Both entered the
/// tree, split the real window's half into a quarter, and left it there.
/// </para>
/// <code>
/// 0x1702AC  WinUIDesktopWin32WindowClass      GetParent=0x0       WS_CHILD=False
/// 0x70988   InputNonClientPointerSource       GetParent=0x1702AC  WS_CHILD=True
/// 0xA0978   ReunionWindowingCaptionControls   GetParent=0x1702AC  WS_CHILD=True
/// </code>
/// <para>
/// The asymmetry that made this invisible: restarting with Clipchamp already open lays it out
/// CORRECTLY, at its full half. Adoption enumerates through <c>EnumWindows</c>, which by
/// construction returns only top-level windows and therefore never offered the chrome. The event
/// path takes a raw HWND from a desktop-wide hook, which reports child windows too, and nothing
/// downstream asked whether the thing was top-level at all.
/// </para>
/// <para>
/// So the two admission paths disagreed about what a window is, and the fix is to make them agree
/// rather than to name the offenders. A class-name list is always one release behind; "not a child"
/// is what <c>EnumWindows</c> already enforces, and it is true of the next one nobody has met yet.
/// Photoshop's 108x24 <c>Button</c>, measured separately and never explained, is the same shape.
/// </para>
/// </remarks>
public sealed class ChildWindowTrackabilityTests
{
    /// <summary>The measured shape: unowned, uncloaked, and a child. Every earlier guard says yes.</summary>
    [Fact]
    public void AChildWindowThatIsUnownedAndUncloaked_IsNotTrackable()
    {
        Assert.False(Win32NativeWindowSource.IsTrackable(hasOwner: false, isCloaked: false, isChild: true));
    }

    /// <summary>The real window beside it, which must keep being tracked.</summary>
    [Fact]
    public void ATopLevelWindowThatIsUnownedAndUncloaked_IsStillTrackable()
    {
        Assert.True(Win32NativeWindowSource.IsTrackable(hasOwner: false, isCloaked: false, isChild: false));
    }

    /// <summary>Being top-level rescues nothing that the existing two halves already reject.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void TheExistingRejections_AreUnchangedForTopLevelWindows(bool hasOwner, bool isCloaked)
    {
        Assert.False(Win32NativeWindowSource.IsTrackable(hasOwner, isCloaked, isChild: false));
    }

    /// <summary>Child-ness alone is enough; it does not need help from the other two.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AChildIsRejectedWhateverElseIsTrue(bool hasOwner, bool isCloaked)
    {
        Assert.False(Win32NativeWindowSource.IsTrackable(hasOwner, isCloaked, isChild: true));
    }
}
