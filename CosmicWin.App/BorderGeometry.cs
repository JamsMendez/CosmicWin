using CosmicWin.Interop;

namespace CosmicWin.App;

/// <summary>
/// Where the focus border is drawn, and how thick it is allowed to get.
/// </summary>
/// <remarks>
/// Pure arithmetic. The border is drawn OUTSIDE the window rather than over it, so it never covers
/// content the application put at its own edge -- possible only because <see cref="TreeArranger"/>
/// already insets every tile by <c>Gap / 2</c>, leaving exactly <c>Gap</c> between neighbours.
/// </remarks>
public static class BorderGeometry
{
    /// <summary>
    /// How thick the focus border is drawn. Two pixels on top of the one DWM already draws, which is
    /// what the maintainer asked for after seeing the native border was too thin to read.
    /// </summary>
    /// <remarks>
    /// DWM cannot be asked for this. Its 29 window attributes include
    /// <c>DWMWA_VISIBLE_FRAME_BORDER_THICKNESS</c>, which is a GET -- it reports how many pixels DWM
    /// draws and will not be told. There is no thickness attribute at all, so a thicker border has
    /// to be drawn rather than requested.
    /// </remarks>
    public const int DefaultThickness = 2;

    /// <summary>
    /// The thickest border that still fits between two tiles: half the gap, since both neighbours
    /// grow toward each other. Anything past it makes their borders overlap, and against the work
    /// area it falls off the screen.
    /// </summary>
    public static int MaxThicknessForGap(int gap) => Math.Max(0, gap / 2);

    /// <summary>
    /// The rectangle to draw the border in: the window's own, grown by
    /// <paramref name="thickness"/> on every side.
    /// </summary>
    /// <remarks>
    /// A negative thickness is clamped to zero rather than trusted. Left alone it would SHRINK the
    /// rectangle and draw the border inside the window, over content -- and the value arrives here
    /// from a constant somebody will eventually edit.
    /// </remarks>
    public static Rectangle Around(Rectangle window, int thickness)
    {
        var grow = Math.Max(0, thickness);

        return new Rectangle(
            window.Left - grow,
            window.Top - grow,
            window.Right + grow,
            window.Bottom + grow);
    }

    /// <summary>
    /// The radius Windows 11 rounds an ordinary window's corners to.
    /// </summary>
    /// <remarks>
    /// A design value, not a measurement, because the radius is not queryable.
    /// <c>DWMWA_WINDOW_CORNER_PREFERENCE</c> can be read and written, but it carries a PREFERENCE --
    /// round, round-small, do-not-round -- and never a number of pixels. Windows uses 8 for round
    /// and 4 for round-small; this follows the ordinary one.
    /// </remarks>
    public const int WindowsCornerRadius = 8;

    /// <summary>
    /// The radius the border's own corner needs to stay concentric with the window's.
    /// </summary>
    /// <remarks>
    /// The border sits OUTSIDE the window, so its corner traces a larger arc. Reusing the window's
    /// radius unchanged would draw a tighter curve than the one it follows, and the distance between
    /// the two would visibly widen at forty-five degrees while staying correct on the straight runs.
    /// </remarks>
    public static int CornerRadiusAround(int windowRadius, int thickness) =>
        Math.Max(0, windowRadius) + Math.Max(0, thickness);
}
