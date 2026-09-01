using CosmicWin.Interop.Win32;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// MR-1 (first real run, cosmic-win): pins the pure trackability decision extracted
/// from <see cref="Win32NativeWindowSource.IsTrackable(HWND)"/>'s two raw Win32 reads against the
/// exact descriptor shape a real desktop enumeration measured -- <c>ApplicationFrameWindow</c>
/// (owned by <c>explorer.exe</c>, unowned per <c>GW_OWNER</c>, DWM-cloaked) silently passing every
/// prior check and occupying a tiling slot that never rendered anything.
/// </summary>
/// <remarks>
/// These facts exercise the extracted pure predicate rather than a live HWND. The reason used to be
/// that DWM cloaking could not be self-triggered by a spawned test window; that stopped being true
/// once <c>Win32VirtualDesktopService.TrySwitchTo</c> existed, and
/// <c>DesktopSwitchVisibilityTests</c> now cloaks a real window on purpose and waits for it.
/// <para>
/// The pure predicate is still the right level for THESE facts -- they are headless, exhaustive over
/// the four input combinations, and cost nothing -- but the justification is now "cheapest level
/// that proves the decision", not "no other level is possible". A comment that keeps claiming
/// something is impossible after the repository learned to do it will eventually stop somebody from
/// writing the test that matters.
/// </para>
/// </remarks>
public sealed class Win32NativeWindowSourceCloakingTests
{
    /// <summary>The exact shape measured for the visible, real windows admitted alongside the phantoms.</summary>
    [Fact]
    public void IsTrackable_UnownedAndNotCloaked_ReturnsTrue()
    {
        Assert.True(Win32NativeWindowSource.IsTrackable(hasOwner: false, isCloaked: false, isChild: false));
    }

    /// <summary>
    /// The exact shape measured for the real desktop's phantom <c>ApplicationFrameWindow</c>
    /// (explorer.exe, unowned, cloaked) -- the window that used to silently take a tiling slot.
    /// </summary>
    [Fact]
    public void IsTrackable_UnownedButCloaked_ReturnsFalse()
    {
        Assert.False(Win32NativeWindowSource.IsTrackable(hasOwner: false, isCloaked: true, isChild: false));
    }

    [Fact]
    public void IsTrackable_Owned_ReturnsFalseRegardlessOfCloakState()
    {
        Assert.False(Win32NativeWindowSource.IsTrackable(hasOwner: true, isCloaked: false, isChild: false));
        Assert.False(Win32NativeWindowSource.IsTrackable(hasOwner: true, isCloaked: true, isChild: false));
    }
}
