using CosmicWin.App.Alerts;
using CosmicWin.Interop;

namespace CosmicWin.App.Tests.Alerts;

/// <summary>
/// Where <see cref="AlertTileLayout"/> places N alert tiles over the target area, decided
/// 2026-09-23 (plan &#167;4/&#167;6, T3).
/// </summary>
/// <remarks>
/// Pure arithmetic, no Win32: given the tile list (already expanded from the parsed groups, in
/// command order) and the back-buffer size, it returns one non-overlapping, in-bounds rectangle
/// per tile, also in command order. <see cref="Rectangle"/> is <c>CosmicWin.Interop</c>'s own
/// geometry type; it is reused here rather than duplicated, the same way <c>BorderGeometry</c>
/// already does across the App/Interop boundary for a type with no Win32 in it.
/// </remarks>
public sealed class AlertTileLayoutTests
{
    /// <summary>The area T0 measured on hardware: an ultrawide back buffer, primary work area.</summary>
    private static readonly (int Width, int Height) Ultrawide = (3392, 1440);

    private static IReadOnlyList<AlertKind> Tiles(int count) =>
        Enumerable.Repeat(AlertKind.Warning, count).ToArray();

    [Fact]
    public void ZeroTiles_IsAnEmptyLayout()
    {
        Assert.Empty(AlertTileLayout.Layout(Tiles(0), Ultrawide.Width, Ultrawide.Height));
    }

    [Theory]
    [InlineData(0, 1440)]
    [InlineData(3392, 0)]
    [InlineData(-100, 1440)]
    [InlineData(3392, -100)]
    public void ADegenerateArea_IsAnEmptyLayout(int width, int height)
    {
        Assert.Empty(AlertTileLayout.Layout(Tiles(3), width, height));
    }

    /// <summary>One tile is one big rectangle, centered, and NOT the whole back buffer -- margin applies.</summary>
    [Fact]
    public void OneTile_IsOneBigCenteredTile()
    {
        var rects = AlertTileLayout.Layout(Tiles(1), Ultrawide.Width, Ultrawide.Height);

        var rect = Assert.Single(rects);
        // Hand-computed: margin/gap = round(2% of 1440) = 29. The only 16:9 tile that fits the
        // one remaining cell (3334x1382) is height-bound: height 1382, width floor(1382*16/9) = 2456.
        Assert.Equal(2456, rect.Width);
        Assert.Equal(1382, rect.Height);
        // Centered in the full area (integer division truncates the odd remainder by one pixel).
        Assert.Equal((Ultrawide.Width - rect.Width) / 2, rect.Left);
        Assert.Equal((Ultrawide.Height - rect.Height) / 2, rect.Top);
    }

    /// <summary>16:9 is the target aspect; a single tile should land close to it, not stretched.</summary>
    [Fact]
    public void OneTile_KeepsRoughlyA16By9Aspect()
    {
        var rect = Assert.Single(AlertTileLayout.Layout(Tiles(1), Ultrawide.Width, Ultrawide.Height));

        var aspect = (double)rect.Width / rect.Height;
        Assert.InRange(aspect, 16.0 / 9.0 - 0.01, 16.0 / 9.0 + 0.01);
    }

    /// <summary>4 tiles on a 16:9 area fit best as a 2x2 grid -- two distinct rows, two distinct columns.</summary>
    [Fact]
    public void FourTiles_On16By9_FormATwoByTwoGrid()
    {
        var rects = AlertTileLayout.Layout(Tiles(4), 1920, 1080);

        Assert.Equal(4, rects.Count);
        Assert.Equal(2, rects.Select(r => r.Top).Distinct().Count());
        Assert.Equal(2, rects.Select(r => r.Left).Distinct().Count());
    }

    /// <summary>
    /// 3 tiles on 1920x1080 fit best as 2 columns x 2 rows (hand-verified: it beats 1x3 and 3x1 on
    /// tile area). The last row holds one tile, which is centered under the pair above it.
    /// </summary>
    [Fact]
    public void APartialLastRow_IsCenteredUnderTheRowsAboveIt()
    {
        var rects = AlertTileLayout.Layout(Tiles(3), 1920, 1080);

        Assert.Equal(3, rects.Count);
        var firstRow = rects.Take(2).ToArray();
        var lastRow = rects.Skip(2).ToArray();

        Assert.Single(lastRow);
        var pairCenter = (firstRow[0].Left + firstRow[1].Right) / 2;
        var lastCenter = (lastRow[0].Left + lastRow[0].Right) / 2;
        Assert.InRange(Math.Abs(pairCenter - lastCenter), 0, 1);
    }

    [Fact]
    public void TilesAreReturnedInCommandOrder()
    {
        var tiles = new[] { AlertKind.Warning, AlertKind.Failed, AlertKind.Warning, AlertKind.Failed };
        var rects = AlertTileLayout.Layout(tiles, 1920, 1080);

        // Row-major fill means the first two tiles (command order) are the first row.
        Assert.Equal(rects[0].Top, rects[1].Top);
        Assert.True(rects[0].Left < rects[1].Left);
    }

    public static IEnumerable<object[]> AreasAndCounts()
    {
        (int Width, int Height)[] areas =
        [
            (3392, 1440), // ultrawide, the measured hardware area
            (1920, 1080), // 16:9
            (1080, 1920), // portrait
        ];

        foreach (var area in areas)
        {
            for (var count = 1; count <= 16; count++)
            {
                yield return [area.Width, area.Height, count];
            }
        }
    }

    [Theory]
    [MemberData(nameof(AreasAndCounts))]
    public void EveryTile_IsFullyInsideTheArea(int width, int height, int count)
    {
        var area = Rectangle.FromSize(0, 0, width, height);

        foreach (var rect in AlertTileLayout.Layout(Tiles(count), width, height))
        {
            Assert.True(area.Contains(rect), $"{rect} is not inside {area} (N={count})");
        }
    }

    [Theory]
    [MemberData(nameof(AreasAndCounts))]
    public void NoTwoTiles_Overlap(int width, int height, int count)
    {
        var rects = AlertTileLayout.Layout(Tiles(count), width, height);

        for (var i = 0; i < rects.Count; i++)
        {
            for (var j = i + 1; j < rects.Count; j++)
            {
                var a = rects[i];
                var b = rects[j];
                var overlapsHorizontally = a.Left < b.Right && b.Left < a.Right;
                var overlapsVertically = a.Top < b.Bottom && b.Top < a.Bottom;
                Assert.False(
                    overlapsHorizontally && overlapsVertically,
                    $"{a} overlaps {b} (N={count}, area={width}x{height})");
            }
        }
    }

    [Theory]
    [MemberData(nameof(AreasAndCounts))]
    public void TheTileCountMatchesTheInput(int width, int height, int count)
    {
        Assert.Equal(count, AlertTileLayout.Layout(Tiles(count), width, height).Count);
    }

    /// <summary>
    /// T11: the host window (and its back buffer) now spans the WHOLE monitor, not just the work
    /// area <see cref="Layout"/> was given -- so a work area that does not start at the monitor's
    /// origin (taskbar docked left or top) needs its tiles shifted by that offset before they are
    /// handed to the overlay, which draws directly in back-buffer coordinates. Pure translation, kept
    /// separate from <see cref="Layout"/>'s own grid arithmetic so the offset alone is testable.
    /// </summary>
    [Fact]
    public void ToBackBufferCoordinates_ShiftsEveryRectByTheGivenOffset()
    {
        var rects = AlertTileLayout.Layout(Tiles(2), 1920, 1080);

        var shifted = AlertTileLayout.ToBackBufferCoordinates(rects, offsetX: 50, offsetY: 30);

        Assert.Equal(rects.Count, shifted.Count);
        for (var i = 0; i < rects.Count; i++)
        {
            Assert.Equal(rects[i].Left + 50, shifted[i].Left);
            Assert.Equal(rects[i].Top + 30, shifted[i].Top);
            Assert.Equal(rects[i].Right + 50, shifted[i].Right);
            Assert.Equal(rects[i].Bottom + 30, shifted[i].Bottom);
            Assert.Equal(rects[i].Width, shifted[i].Width);
            Assert.Equal(rects[i].Height, shifted[i].Height);
        }
    }

    /// <summary>A zero offset (taskbar docked right or bottom: the work area already starts at the monitor's origin) is a no-op.</summary>
    [Fact]
    public void ToBackBufferCoordinates_WithZeroOffset_ReturnsTheSameRectangles()
    {
        var rects = AlertTileLayout.Layout(Tiles(3), 1920, 1080);

        var shifted = AlertTileLayout.ToBackBufferCoordinates(rects, offsetX: 0, offsetY: 0);

        Assert.Equal(rects, shifted);
    }

    [Fact]
    public void ToBackBufferCoordinates_OfAnEmptyLayout_IsStillEmpty()
    {
        Assert.Empty(AlertTileLayout.ToBackBufferCoordinates([], offsetX: 12, offsetY: 7));
    }
}
