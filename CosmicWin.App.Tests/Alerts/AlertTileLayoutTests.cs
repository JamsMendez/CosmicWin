using CosmicWin.App.Alerts;

namespace CosmicWin.App.Tests.Alerts;

/// <summary>
/// alert-tile-mosaic (2026-09-26): <see cref="AlertTileLayout"/> is the pure decision behind the
/// tiled-window-style alert mosaic -- turning a parsed <see cref="AlertCommand"/> into the ordered
/// tile kinds to show (failed first, capped at 8) and the grid to place them in (feature doc,
/// "Decisions" &#167;1/&#167;2). Unit-tested directly so <c>AppComposition</c>/
/// <c>WebViewAlertLayerController</c> never have to be exercised to prove this arithmetic -- same
/// split the rest of the alert-layer code already uses.
/// </summary>
public sealed class AlertTileLayoutTests
{
    private static AlertCommand Command(params AlertGroup[] groups) =>
        new(groups, TimeSpan.FromSeconds(5));

    /// <summary>The maintainer's grid table, feature doc decision 2, every row.</summary>
    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 2, 1)]
    [InlineData(3, 2, 2)]
    [InlineData(4, 2, 2)]
    [InlineData(5, 3, 2)]
    [InlineData(6, 3, 2)]
    [InlineData(7, 4, 2)]
    [InlineData(8, 4, 2)]
    public void GridMatchesTheMaintainersTable(int totalTiles, int expectedColumns, int expectedRows)
    {
        var command = Command(new AlertGroup(AlertKind.Warning, totalTiles));

        var layout = AlertTileLayout.From(command);

        Assert.Equal(expectedColumns, layout.Columns);
        Assert.Equal(expectedRows, layout.Rows);
        Assert.Equal(totalTiles, layout.Tiles.Count);
    }

    [Fact]
    public void MoreThanEightRequestedStillUsesTheEightPlusGrid()
    {
        var command = Command(new AlertGroup(AlertKind.Warning, 16));

        var layout = AlertTileLayout.From(command);

        Assert.Equal(4, layout.Columns);
        Assert.Equal(2, layout.Rows);
        Assert.Equal(8, layout.Tiles.Count);
    }

    [Fact]
    public void FailedTilesAlwaysPrecedeWarnings()
    {
        var command = Command(new AlertGroup(AlertKind.Warning, 2), new AlertGroup(AlertKind.Failed, 3));

        var layout = AlertTileLayout.From(command);

        Assert.Equal(
            new[] { AlertKind.Failed, AlertKind.Failed, AlertKind.Failed, AlertKind.Warning, AlertKind.Warning },
            layout.Tiles);
    }

    [Fact]
    public void FailedTilesAlonePreserveCountAndGrid()
    {
        var command = Command(new AlertGroup(AlertKind.Failed, 3));

        var layout = AlertTileLayout.From(command);

        Assert.Equal(new[] { AlertKind.Failed, AlertKind.Failed, AlertKind.Failed }, layout.Tiles);
        Assert.Equal(2, layout.Columns);
        Assert.Equal(2, layout.Rows);
    }

    /// <summary>Decision 3: past 8 slots the rest is dropped, so failed always wins over warning.</summary>
    [Fact]
    public void EightFailedPlusOneWarning_DropsTheWarningEntirely()
    {
        var command = Command(new AlertGroup(AlertKind.Failed, 8), new AlertGroup(AlertKind.Warning, 1));

        var layout = AlertTileLayout.From(command);

        Assert.Equal(8, layout.Tiles.Count);
        Assert.All(layout.Tiles, kind => Assert.Equal(AlertKind.Failed, kind));
    }

    /// <summary>Decision 3: a partial cut still keeps every failed tile before it starts dropping warnings.</summary>
    [Fact]
    public void SixFailedPlusFiveWarning_KeepsAllFailedAndFillsTheRestWithWarning()
    {
        var command = Command(new AlertGroup(AlertKind.Failed, 6), new AlertGroup(AlertKind.Warning, 5));

        var layout = AlertTileLayout.From(command);

        Assert.Equal(8, layout.Tiles.Count);
        Assert.Equal(6, layout.Tiles.Count(kind => kind == AlertKind.Failed));
        Assert.Equal(2, layout.Tiles.Count(kind => kind == AlertKind.Warning));
        Assert.Equal(AlertKind.Failed, layout.Tiles[0]);
        Assert.Equal(AlertKind.Warning, layout.Tiles[7]);
    }

    /// <summary>16 total (the parser's own max, per its grammar) still caps the display at 8, failed first regardless of command order.</summary>
    [Fact]
    public void SixteenTotal_CapsAtEightRegardlessOfKindSplitOrCommandOrder()
    {
        var command = Command(new AlertGroup(AlertKind.Warning, 9), new AlertGroup(AlertKind.Failed, 7));

        var layout = AlertTileLayout.From(command);

        Assert.Equal(8, layout.Tiles.Count);
        Assert.Equal(7, layout.Tiles.Count(kind => kind == AlertKind.Failed));
        Assert.Equal(1, layout.Tiles.Count(kind => kind == AlertKind.Warning));
        Assert.Equal(AlertKind.Failed, layout.Tiles[0]);
    }
}
