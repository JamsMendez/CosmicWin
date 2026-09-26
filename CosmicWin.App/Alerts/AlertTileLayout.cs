namespace CosmicWin.App.Alerts;

/// <summary>
/// alert-tile-mosaic (2026-09-26): turns a parsed <see cref="AlertCommand"/> into the ordered tile
/// kinds to show and the grid to place them in, mirroring how the tiling engine lays out windows
/// (outer/inner gap applied by the page, row-major fill). Pure and unit-tested directly
/// (<c>AlertTileLayoutTests</c>) -- <c>AppComposition.UpdateAlertOverlay</c> only calls <see
/// cref="From"/> and forwards the result to the alert layer; nothing here touches a WebView2, a
/// clock, or the desktop.
/// </summary>
/// <remarks>
/// Reuses the name of an EARLIER, unrelated <c>AlertTileLayout</c> that the deleted Direct2D alert
/// overlay consumed -- remove-direct2d-alert-overlay (T1) deleted it as unwired dead code, and
/// <c>RemovedDirect2DAlertOverlayTests</c> used to assert the name stayed gone. That assertion was
/// replaced (see that file's remarks), not silently dropped: this is a new, small, pure type wired
/// LIVE into <c>AppComposition</c>, not the removed one coming back.
/// </remarks>
public sealed record AlertTileLayout(IReadOnlyList<AlertKind> Tiles, int Columns, int Rows)
{
    /// <summary>Never more than this many tiles are shown at once, however many the command asked for (feature doc, decision 1).</summary>
    public const int MaxTiles = 8;

    /// <summary>
    /// Every failed tile first, then every warning tile, capped at <see cref="MaxTiles"/> (feature
    /// doc, decision 3 -- failed always wins past the cap), plus the grid that tile count maps to
    /// (feature doc, decision 2).
    /// </summary>
    public static AlertTileLayout From(AlertCommand command)
    {
        var failedCount = command.Groups.Where(group => group.Kind == AlertKind.Failed).Sum(group => group.Count);
        var warningCount = command.Groups.Where(group => group.Kind == AlertKind.Warning).Sum(group => group.Count);

        var tiles = new List<AlertKind>(Math.Min(failedCount + warningCount, MaxTiles));
        for (var i = 0; i < failedCount && tiles.Count < MaxTiles; i++) tiles.Add(AlertKind.Failed);
        for (var i = 0; i < warningCount && tiles.Count < MaxTiles; i++) tiles.Add(AlertKind.Warning);

        var (columns, rows) = GridFor(tiles.Count);
        return new AlertTileLayout(tiles, columns, rows);
    }

    /// <summary>
    /// The maintainer's grid table (feature doc, decision 2): 1 -&gt; 1x1 (full display, as today);
    /// 2 -&gt; 2 columns x 1 row; 3-4 -&gt; 2x2; 5-6 -&gt; 3x2; 7+ -&gt; 4x2. Slots fill row-major; a
    /// grid with more slots than tiles leaves the rest blank (the page's job, not this one's).
    /// </summary>
    private static (int Columns, int Rows) GridFor(int tileCount) => tileCount switch
    {
        <= 1 => (1, 1),
        2 => (2, 1),
        <= 4 => (2, 2),
        <= 6 => (3, 2),
        _ => (4, 2),
    };
}
