namespace CosmicWin.App.Alerts;

/// <summary>
/// The JSON messages the alert page understands, posted by <see cref="WebViewAlertLayerController"/>.
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

    /// <summary>
    /// see-through-video-tint (S4): <see cref="Show"/> plus <c>"tint":true,"seq":N</c> -- video mode
    /// only, sent when a tint sink is wired. The page echoes <paramref name="seq"/> in its mask so a
    /// late mask can never tint a later alert.
    /// </summary>
    public static string ShowTinted(AlertShowRequest request, int seq) =>
        Show(request)[..^1] + ",\"tint\":true,\"seq\":" + seq + "}";

    /// <summary>
    /// Sent once the tint is actually RENDERED on the video (not merely requested): the page stops
    /// painting the letters.
    /// </summary>
    public static string TintReady(int seq) => "{\"type\":\"tint-ready\",\"seq\":" + seq + "}";

    /// <summary>The tint stopped rendering (failure/back-off): the page must paint its letters again.</summary>
    public static string TintLost(int seq) => "{\"type\":\"tint-lost\",\"seq\":" + seq + "}";
}
