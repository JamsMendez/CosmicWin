using CosmicWin.Interop.Win32;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// Pins how <see cref="Win32NativeWindowSource.TrySetMaximizeBox"/> decides, from the style it reads
/// back, whether its write landed.
/// </summary>
/// <remarks>
/// The read-back used to be compared to the WHOLE wanted style. A window whose app or Windows flips
/// another bit at the same moment (visibility, the maximized state, a frame bit) then read as
/// "refused" although the box was cleared: the adapter never recorded the window as stripped, so it
/// never gave the button back. Only the maximize-box bit is ours to check. Exercised through the
/// extracted pure predicate, the cheapest level that proves the decision.
/// </remarks>
public sealed class Win32NativeWindowSourceMaximizeBoxTests
{
    private const uint MaximizeBox = 0x00010000u;
    private const uint Visible = 0x10000000u;
    private const uint Maximized = 0x01000000u;
    private const uint Caption = 0x00C00000u;

    [Fact]
    public void Clearing_LandedWhenTheBoxBitIsGone()
    {
        Assert.True(Win32NativeWindowSource.MaximizeBoxApplied(readBack: Caption | Visible, enabled: false));
    }

    [Fact]
    public void Clearing_RefusedWhenTheBoxBitIsStillThere()
    {
        Assert.False(Win32NativeWindowSource.MaximizeBoxApplied(readBack: Caption | Visible | MaximizeBox, enabled: false));
    }

    [Fact]
    public void Setting_LandedWhenTheBoxBitIsBack()
    {
        Assert.True(Win32NativeWindowSource.MaximizeBoxApplied(readBack: Caption | MaximizeBox, enabled: true));
    }

    [Fact]
    public void Setting_RefusedWhenTheBoxBitIsStillMissing()
    {
        Assert.False(Win32NativeWindowSource.MaximizeBoxApplied(readBack: Caption, enabled: true));
    }

    /// <summary>
    /// The regression: another bit changed between the write and the read-back. The box was cleared,
    /// so the write landed, and the window must be recorded as stripped so its button comes back.
    /// </summary>
    [Theory]
    [InlineData(Visible)]
    [InlineData(Maximized)]
    [InlineData(Visible | Maximized)]
    public void Clearing_StillLandedWhenAnotherBitChangedMeanwhile(uint otherBits)
    {
        Assert.True(Win32NativeWindowSource.MaximizeBoxApplied(readBack: Caption | otherBits, enabled: false));
    }

    [Theory]
    [InlineData(Visible)]
    [InlineData(Maximized)]
    public void Setting_StillLandedWhenAnotherBitChangedMeanwhile(uint otherBits)
    {
        Assert.True(Win32NativeWindowSource.MaximizeBoxApplied(readBack: Caption | MaximizeBox | otherBits, enabled: true));
    }
}
