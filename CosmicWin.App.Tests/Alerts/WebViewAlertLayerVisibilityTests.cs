using CosmicWin.App.Alerts;

namespace CosmicWin.App.Tests.Alerts;

/// <summary>
/// D3 (html-wallpaper-demo, demo/html-wallpaper-d3-switch): <see cref="WebViewAlertLayerVisibility"/>
/// is the PURE decision behind <see cref="WebViewAlertLayerController"/>'s real WebView2 IsVisible
/// branches, pulled out for the same reason <see cref="AlertLayerPreloadState"/> already is -- the
/// controller itself only ever runs against real WebView2, a hardware-only concern (see that class's
/// remarks), so the decision belongs here where it can be unit-tested directly.
/// </summary>
public sealed class WebViewAlertLayerVisibilityTests
{
    /// <summary>
    /// In html wallpaper mode the page IS the wallpaper: becoming ready must show it on its own, with
    /// no Start ever required. In video mode (today's only behaviour before D3) the layer stays
    /// hidden until an alert actually starts it.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ShowOnReady_IsTrueOnlyInHtmlWallpaperMode(bool htmlWallpaperMode, bool expected)
    {
        Assert.Equal(expected, WebViewAlertLayerVisibility.ShowOnReady(htmlWallpaperMode));
    }

    /// <summary>
    /// In html wallpaper mode neither <c>End()</c> nor the page's own "done" message may hide the
    /// layer -- it must stay visible permanently once ready. In video mode both still hide it, exactly
    /// as before D3.
    /// </summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void HideOnEndOrDone_IsFalseOnlyInHtmlWallpaperMode(bool htmlWallpaperMode, bool expected)
    {
        Assert.Equal(expected, WebViewAlertLayerVisibility.HideOnEndOrDone(htmlWallpaperMode));
    }
}
