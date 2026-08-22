using CosmicWin.Interop.Win32;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// MR-1 (2026-08-22 first real run, cosmic-win): pins the pure trackability decision extracted
/// from <see cref="Win32NativeWindowSource.IsTrackable(HWND)"/>'s two raw Win32 reads against the
/// exact descriptor shape a real desktop enumeration measured -- <c>ApplicationFrameWindow</c>
/// (owned by <c>explorer.exe</c>, unowned per <c>GW_OWNER</c>, DWM-cloaked) silently passing every
/// prior check and occupying a tiling slot that never rendered anything. DWM cloaking cannot be
/// self-triggered by a spawned test window, so this exercises the extracted pure predicate rather
/// than a live HWND.
/// </summary>
public sealed class Win32NativeWindowSourceCloakingTests
{
    /// <summary>The exact shape measured for the visible, real windows admitted alongside the phantoms.</summary>
    [Fact]
    public void IsTrackable_UnownedAndNotCloaked_ReturnsTrue()
    {
        Assert.True(Win32NativeWindowSource.IsTrackable(hasOwner: false, isCloaked: false));
    }

    /// <summary>
    /// The exact shape measured for the real desktop's phantom <c>ApplicationFrameWindow</c>
    /// (explorer.exe, unowned, cloaked) -- the window that used to silently take a tiling slot.
    /// </summary>
    [Fact]
    public void IsTrackable_UnownedButCloaked_ReturnsFalse()
    {
        Assert.False(Win32NativeWindowSource.IsTrackable(hasOwner: false, isCloaked: true));
    }

    [Fact]
    public void IsTrackable_Owned_ReturnsFalseRegardlessOfCloakState()
    {
        Assert.False(Win32NativeWindowSource.IsTrackable(hasOwner: true, isCloaked: false));
        Assert.False(Win32NativeWindowSource.IsTrackable(hasOwner: true, isCloaked: true));
    }
}
