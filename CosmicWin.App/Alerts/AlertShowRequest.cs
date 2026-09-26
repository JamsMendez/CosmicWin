namespace CosmicWin.App.Alerts;

/// <summary>
/// alert-tile-mosaic (2026-09-26): one "show" request for the preloaded alert layer -- the ordered
/// tile kinds to draw (wire strings <c>"warning"</c>/<c>"failed"</c>, failed first and capped at 8,
/// per <see cref="AlertTileLayout"/>), the grid to place them in, and the gap to draw around and
/// between them.
/// </summary>
/// <remarks>
/// <see cref="Gap"/> is read from <c>TreeArranger.Gap</c> at show time (feature doc, "Gap"), in
/// PHYSICAL pixels -- the same unit the tiling engine's own gap setting uses -- so a settings change
/// takes effect on the NEXT alert without this record, <see cref="AlertLayerPreloadState"/>, or
/// <see cref="WebViewAlertLayerController"/> having to know about settings at all. The page
/// (<c>alert-layer.js</c>) converts it to CSS pixels with <c>devicePixelRatio</c>.
/// <para>
/// Carried end to end unchanged from <c>AppComposition.UpdateAlertOverlay</c> through <see
/// cref="AlertLayerPreloadState"/>'s pending/shown tracking to <see
/// cref="WebViewAlertLayerController.PostShow"/>, which is the only place it is serialized into the
/// <c>{type:"show",...}</c> WebView2 message -- replaces the old single <c>string kind</c> that
/// carried only ONE collapsed kind, throwing away every per-kind count the parser already accepted.
/// </para>
/// </remarks>
public sealed record AlertShowRequest(
    IReadOnlyList<string> Tiles, int Columns, int Rows, int Gap, int DurationMilliseconds);
