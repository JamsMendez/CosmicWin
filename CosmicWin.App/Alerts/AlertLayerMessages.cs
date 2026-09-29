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
