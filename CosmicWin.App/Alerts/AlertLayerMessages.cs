namespace CosmicWin.App.Alerts;

/// <summary>
/// The JSON messages the scene/alert pages understand, shared by every WebView2 layer that forwards
/// alerts to a page (<see cref="WebViewAlertLayerController"/> and the mini scene window).
/// </summary>
internal static class AlertLayerMessages
{
    /// <summary>Tells the page to hide its alert overlay.</summary>
    public const string Hide = "{\"type\":\"hide\"}";

    /// <summary>
    /// Tells the html wallpaper scene page to stop drawing and arm no animation frame while a
    /// fullscreen window covers the desktop (shared/js/render-loop.js setWallpaperPaused). Never sent
    /// to the mini scene window, which is always visible.
    /// </summary>
    public const string Pause = "{\"type\":\"pause\"}";

    /// <summary>Tells a paused html wallpaper scene page to start drawing again.</summary>
    public const string Resume = "{\"type\":\"resume\"}";

    /// <summary>
    /// The <c>{type:"show",...}</c> message for <paramref name="request"/>. The work area is physical
    /// pixels relative to the layer surface, all zero when unknown (the page then lays out on the
    /// whole canvas), and clamped to zero or more so a bad rect can never fail an alert.
    /// </summary>
    public static string Show(AlertShowRequest request)
    {
        var tilesJson = string.Join(",", request.Tiles.Select(tile => $"\"{tile}\""));
        var workAreaJson = "{\"left\":" + Math.Max(0, request.WorkAreaLeft)
            + ",\"top\":" + Math.Max(0, request.WorkAreaTop)
            + ",\"width\":" + Math.Max(0, request.WorkAreaWidth)
            + ",\"height\":" + Math.Max(0, request.WorkAreaHeight) + "}";
        return $"{{\"type\":\"show\",\"tiles\":[{tilesJson}],\"columns\":{request.Columns},"
            + $"\"rows\":{request.Rows},\"gap\":{request.Gap},\"workArea\":{workAreaJson},"
            + $"\"duration\":{request.DurationMilliseconds}}}";
    }
}
