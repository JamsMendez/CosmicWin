using CosmicWin.Interop;

namespace CosmicWin.App.Alerts;

/// <summary>
/// Lays out N alert tiles over the video wallpaper's back buffer: one non-overlapping, in-bounds
/// rectangle per tile, in the order the tiles were given.
/// </summary>
/// <remarks>
/// <para>
/// Pure arithmetic, no Win32 -- <see cref="AlertCommandParser"/> and the queue (T2, not yet
/// built) decide WHICH tiles and WHEN; this only decides WHERE. It takes the already-expanded tile
/// list (for example <c>warning, warning, failed</c>) rather than an <see cref="AlertCommand"/>,
/// so it stays testable with no knowledge of the command grammar at all.
/// </para>
/// <para>
/// Grid choice, decided 2026-09-23 (plan &#167;4/&#167;6, T3): every column count from 1 to N is
/// tried, each paired with the row count it forces (<c>ceil(N / cols)</c>), and the one that
/// yields the LARGEST 16:9 tile wins -- ties broken toward fewer rows, which in practice means
/// the wider of the tied grids. Every tile keeps a 16:9 aspect regardless of the area's own shape,
/// so a tile always reads the same regardless of how many others share the screen with it.
/// </para>
/// </remarks>
public static class AlertTileLayout
{
    /// <summary>The aspect every tile is drawn at, independent of the area's own shape.</summary>
    private const double TileAspect = 16.0 / 9.0;

    /// <summary>How much of the shorter area side the outer margin and the inter-tile gap each take.</summary>
    private const double MarginFraction = 0.02;

    public static IReadOnlyList<Rectangle> Layout(IReadOnlyList<AlertKind> tiles, int width, int height)
    {
        var count = tiles.Count;
        if (count == 0 || width <= 0 || height <= 0)
        {
            return [];
        }

        var margin = (int)Math.Round(MarginFraction * Math.Min(width, height), MidpointRounding.AwayFromZero);
        // Margin and gap share the same formula (plan &#167;6): "applied consistently" means one
        // value, used both around the grid and between neighbouring tiles.
        var gap = margin;

        var best = FindBestGrid(count, width, height, margin, gap);

        return PlaceTiles(count, width, height, gap, best);
    }

    /// <summary>One candidate grid: its shape, and the largest 16:9 tile that fits it.</summary>
    private readonly record struct GridCandidate(int Columns, int Rows, double TileWidth, double TileHeight)
    {
        public double TileArea => TileWidth * TileHeight;
    }

    /// <summary>
    /// Tries every column count from 1 to <paramref name="count"/> and keeps the one whose forced
    /// row count yields the largest tile, breaking a tie toward fewer rows.
    /// </summary>
    private static GridCandidate FindBestGrid(int count, int width, int height, int margin, int gap)
    {
        GridCandidate? best = null;

        for (var columns = 1; columns <= count; columns++)
        {
            var rows = (count + columns - 1) / columns;

            var availableWidth = width - 2 * margin - (columns - 1) * gap;
            var availableHeight = height - 2 * margin - (rows - 1) * gap;
            if (availableWidth <= 0 || availableHeight <= 0)
            {
                continue;
            }

            var cellWidth = (double)availableWidth / columns;
            var cellHeight = (double)availableHeight / rows;

            // The largest 16:9 rectangle that fits inside (cellWidth, cellHeight): bound by
            // whichever side of the cell is the tighter fit against the target aspect.
            double tileWidth, tileHeight;
            if (cellWidth / cellHeight > TileAspect)
            {
                tileHeight = cellHeight;
                tileWidth = cellHeight * TileAspect;
            }
            else
            {
                tileWidth = cellWidth;
                tileHeight = cellWidth / TileAspect;
            }

            var candidate = new GridCandidate(columns, rows, tileWidth, tileHeight);
            if (best is null
                || candidate.TileArea > best.Value.TileArea
                || (candidate.TileArea == best.Value.TileArea && candidate.Rows < best.Value.Rows))
            {
                best = candidate;
            }
        }

        // Reached only when the area is too small for even a margin plus one column of tiles to
        // fit -- degenerate, but not the width/height <= 0 case Layout already rejects outright.
        // A single column of zero-sized tiles stays in-bounds and non-overlapping rather than
        // throwing.
        return best ?? new GridCandidate(1, count, 0, 0);
    }

    private static IReadOnlyList<Rectangle> PlaceTiles(
        int count, int width, int height, int gap, GridCandidate grid)
    {
        var tileWidth = Math.Max(0, (int)Math.Floor(grid.TileWidth));
        var tileHeight = Math.Max(0, (int)Math.Floor(grid.TileHeight));

        var gridWidth = grid.Columns * tileWidth + (grid.Columns - 1) * gap;
        var gridHeight = grid.Rows * tileHeight + (grid.Rows - 1) * gap;
        var originX = (width - gridWidth) / 2;
        var originY = (height - gridHeight) / 2;

        var rects = new List<Rectangle>(count);
        var placed = 0;

        for (var row = 0; row < grid.Rows && placed < count; row++)
        {
            var tilesInRow = Math.Min(grid.Columns, count - placed);
            var rowWidth = tilesInRow * tileWidth + (tilesInRow - 1) * gap;
            // A full row spans the whole grid width already; only the last, possibly partial, row
            // needs re-centering under the rows above it.
            var rowStartX = originX + (gridWidth - rowWidth) / 2;
            var rowY = originY + row * (tileHeight + gap);

            for (var column = 0; column < tilesInRow; column++)
            {
                var x = rowStartX + column * (tileWidth + gap);
                rects.Add(Rectangle.FromSize(x, rowY, tileWidth, tileHeight));
            }

            placed += tilesInRow;
        }

        return rects;
    }
}
