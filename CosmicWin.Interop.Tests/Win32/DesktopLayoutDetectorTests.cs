using CosmicWin.Interop.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// The one real decision in the Progman attach sequence, pinned without a live desktop: does
/// <see cref="DesktopLayoutDetector.IsRaisedDesktop"/> isolate exactly the
/// <c>WS_EX_NOREDIRECTIONBITMAP</c> bit from Progman's raw <c>GWL_EXSTYLE</c>.
/// </summary>
public sealed class DesktopLayoutDetectorTests
{
    private static readonly uint NoRedirectionBitmap = (uint)WINDOW_EX_STYLE.WS_EX_NOREDIRECTIONBITMAP;

    [Fact]
    public void IsRaisedDesktop_WhenNoRedirectionBitmapIsSet_ReturnsTrue()
    {
        Assert.True(DesktopLayoutDetector.IsRaisedDesktop(NoRedirectionBitmap));
    }

    [Fact]
    public void IsRaisedDesktop_WhenExStyleIsZero_ReturnsFalse()
    {
        Assert.False(DesktopLayoutDetector.IsRaisedDesktop(0));
    }

    [Fact]
    public void IsRaisedDesktop_WhenTheBitIsSetAlongsideOtherBits_StillReturnsTrue()
    {
        var combined = NoRedirectionBitmap | (uint)WINDOW_EX_STYLE.WS_EX_TOPMOST | (uint)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW;

        Assert.True(DesktopLayoutDetector.IsRaisedDesktop(combined));
    }

    [Fact]
    public void IsRaisedDesktop_WhenEveryOtherBitIsSetButNotThisOne_ReturnsFalse()
    {
        var everyOtherBit = ~NoRedirectionBitmap;

        Assert.False(DesktopLayoutDetector.IsRaisedDesktop(everyOtherBit));
    }

    [Theory]
    [InlineData(0u, false)]
    [InlineData(0xFFFFFFFFu, true)]
    public void IsRaisedDesktop_Boundaries(uint exStyle, bool expected)
    {
        Assert.Equal(expected, DesktopLayoutDetector.IsRaisedDesktop(exStyle));
    }
}
