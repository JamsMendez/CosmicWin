using CosmicWin.Layout;

namespace CosmicWin.App.Wallpaper;

/// <summary>
/// Pure placement math for the mini scene window (<see cref="WallpaperMode.Mini"/>): a square,
/// <c>monitorHeight / 5</c> on a side, flush with one corner of the work area so it stays clear of
/// the taskbar. No Win32 and no UI, so the geometry is testable without a desktop.
/// </summary>
public static class MiniWindowPlacement
{
    /// <summary>The window's side is this fraction of the monitor's height.</summary>
    public const int SideDivisor = 5;

    /// <summary>
    /// The window rectangle for <paramref name="corner"/> of <paramref name="workArea"/>. The side never
    /// exceeds the work area's shorter dimension and is never negative, so a degenerate monitor height or a
    /// tiny work area yields a smaller (possibly empty) window that still lies inside the work area.
    /// </summary>
    public static Rect Compute(Rect workArea, int monitorHeight, MiniCorner corner)
    {
        var side = Math.Max(0, Math.Min(monitorHeight / SideDivisor, Math.Min(workArea.Width, workArea.Height)));
        var x = corner is MiniCorner.TopRight or MiniCorner.BottomRight
            ? workArea.X + workArea.Width - side
            : workArea.X;
        var y = corner is MiniCorner.BottomLeft or MiniCorner.BottomRight
            ? workArea.Y + workArea.Height - side
            : workArea.Y;

        return new Rect(x, y, side, side);
    }

    /// <summary>The next corner, clockwise: top-left, top-right, bottom-right, bottom-left, and around.</summary>
    public static MiniCorner Next(MiniCorner corner) => corner switch
    {
        MiniCorner.TopLeft => MiniCorner.TopRight,
        MiniCorner.TopRight => MiniCorner.BottomRight,
        MiniCorner.BottomRight => MiniCorner.BottomLeft,
        _ => MiniCorner.TopLeft,
    };
}
