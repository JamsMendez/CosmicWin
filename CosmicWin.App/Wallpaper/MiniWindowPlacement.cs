using CosmicWin.Layout;

namespace CosmicWin.App.Wallpaper;

/// <summary>
/// Pure placement math for the mini scene window (<see cref="WallpaperMode.HtmlMini"/>): a square,
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
    public static Rect Compute(Rect workArea, int monitorHeight, MiniPosition corner)
    {
        var side = Math.Max(0, Math.Min(monitorHeight / SideDivisor, Math.Min(workArea.Width, workArea.Height)));
        var x = corner switch
        {
            MiniPosition.TopRight or MiniPosition.RightCenter or MiniPosition.BottomRight => workArea.X + workArea.Width - side,
            MiniPosition.TopCenter or MiniPosition.BottomCenter => workArea.X + (workArea.Width - side) / 2,
            _ => workArea.X,
        };
        var y = corner switch
        {
            MiniPosition.BottomLeft or MiniPosition.BottomCenter or MiniPosition.BottomRight => workArea.Y + workArea.Height - side,
            MiniPosition.LeftCenter or MiniPosition.RightCenter => workArea.Y + (workArea.Height - side) / 2,
            _ => workArea.Y,
        };

        return new Rect(x, y, side, side);
    }

    /// <summary>The next position, clockwise through all eight: corners and side midpoints, and around.</summary>
    public static MiniPosition Next(MiniPosition corner) => corner switch
    {
        MiniPosition.TopLeft => MiniPosition.TopCenter,
        MiniPosition.TopCenter => MiniPosition.TopRight,
        MiniPosition.TopRight => MiniPosition.RightCenter,
        MiniPosition.RightCenter => MiniPosition.BottomRight,
        MiniPosition.BottomRight => MiniPosition.BottomCenter,
        MiniPosition.BottomCenter => MiniPosition.BottomLeft,
        MiniPosition.BottomLeft => MiniPosition.LeftCenter,
        MiniPosition.LeftCenter => MiniPosition.TopLeft,
        // Not a defined position (a cast int): restart the cycle rather than throw.
        _ => MiniPosition.TopLeft,
    };
}
