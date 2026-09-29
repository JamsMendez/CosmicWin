using CosmicWin.Interop.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CosmicWin.Interop.Tests.Win32;

public sealed class MiniSceneWindowStylesTests
{
    [Fact]
    public void ExtendedStyleIsTopmostToolNoActivateClickThroughLayeredAndNoRedirectionBitmap()
    {
        var style = MiniSceneWindowStyles.ExtendedStyle;

        Assert.Equal(
            WINDOW_EX_STYLE.WS_EX_TOPMOST | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE
            | WINDOW_EX_STYLE.WS_EX_TRANSPARENT | WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_NOREDIRECTIONBITMAP,
            style);
        Assert.True(style.HasFlag(WINDOW_EX_STYLE.WS_EX_LAYERED)); // click-through needs it, see the style's remarks
        Assert.False(style.HasFlag(WINDOW_EX_STYLE.WS_EX_APPWINDOW));
    }

    [Fact]
    public void PlacementNeverActivatesAndStaysInTheTopmostBand()
    {
        Assert.True(MiniSceneWindowStyles.PlacementFlags.HasFlag(SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE));
        Assert.Equal((nint)(-1), MiniSceneWindowStyles.InsertAfterTopmost);
    }
}
