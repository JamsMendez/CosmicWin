namespace CosmicWin.App.Alerts;

/// <summary>
/// D3 (html-wallpaper-demo, demo/html-wallpaper-d3-switch): the real WebView2 <c>IsVisible</c>
/// decisions <see cref="WebViewAlertLayerController"/> makes, pulled out pure -- the same reasoning
/// <see cref="AlertLayerPreloadState"/> already established for this class: everything that does not
/// need a real WebView2 to decide belongs here, unit-tested directly, leaving the controller a thin
/// shell around it (real environment/controller/navigation stay a hardware-only concern).
/// </summary>
/// <remarks>
/// In html wallpaper mode there is no video underneath the page -- the page itself IS the wallpaper,
/// so it must stay visible permanently once ready: becoming ready shows it with no <c>Start</c>
/// required, and neither <c>End()</c> nor the page's own "done" message may hide it again. In video
/// mode (the only mode before D3) nothing here changes: the layer stays hidden until <c>Start</c>,
/// and both <c>End</c> and "done" hide it, exactly as before.
/// <para>
/// Deliberately separate from <see cref="AlertLayerPreloadState"/>'s own <c>Visible</c> bookkeeping,
/// which keeps tracking "is an alert currently showing" (needed for its pending-show timing) in BOTH
/// modes -- that is a different question from "is the real WebView2 window on screen right now",
/// which in html mode stays true regardless of whether an alert is active.
/// </para>
/// </remarks>
public static class WebViewAlertLayerVisibility
{
    /// <summary>Whether the page becoming ready should make the WebView2 layer visible on its own, with no <c>Start</c> required.</summary>
    public static bool ShowOnReady(bool htmlWallpaperMode) => htmlWallpaperMode;

    /// <summary>Whether <c>End()</c> or the page's own "done" message should hide the WebView2 layer.</summary>
    public static bool HideOnEndOrDone(bool htmlWallpaperMode) => !htmlWallpaperMode;
}
