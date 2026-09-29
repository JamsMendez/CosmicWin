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
        var x = corner switch
        {
            MiniCorner.TopRight or MiniCorner.RightCenter or MiniCorner.BottomRight => workArea.X + workArea.Width - side,
            MiniCorner.TopCenter or MiniCorner.BottomCenter => workArea.X + (workArea.Width - side) / 2,
            _ => workArea.X,
        };
        var y = corner switch
        {
            MiniCorner.BottomLeft or MiniCorner.BottomCenter or MiniCorner.BottomRight => workArea.Y + workArea.Height - side,
            MiniCorner.LeftCenter or MiniCorner.RightCenter => workArea.Y + (workArea.Height - side) / 2,
            _ => workArea.Y,
        };

        return new Rect(x, y, side, side);
    }

    /// <summary>The next position, clockwise through all eight: corners and side midpoints, and around.</summary>
    public static MiniCorner Next(MiniCorner corner) => corner switch
    {
        MiniCorner.TopLeft => MiniCorner.TopCenter,
        MiniCorner.TopCenter => MiniCorner.TopRight,
        MiniCorner.TopRight => MiniCorner.RightCenter,
        MiniCorner.RightCenter => MiniCorner.BottomRight,
        MiniCorner.BottomRight => MiniCorner.BottomCenter,
        MiniCorner.BottomCenter => MiniCorner.BottomLeft,
        MiniCorner.BottomLeft => MiniCorner.LeftCenter,
        _ => MiniCorner.TopLeft,
    };
}
