using CosmicWin.Interop;
using CosmicWin.Layout;

namespace CosmicWin.App;

/// <summary>
/// Where a modal dialog goes when it opens: centred on the work area, at the size it asked for.
/// </summary>
/// <remarks>
/// Pure arithmetic, kept apart from <see cref="TreeArranger"/> on purpose. A dialog is not in the
/// tree and never will be -- <see cref="CosmicWin.Layout.Filters.WindowFilters.IsModalDialog"/>
/// only ever matches windows the tiling filter already refuses -- so it has no siblings to divide a
/// region with and nothing to reflow when it closes.
/// </remarks>
public static class DialogPlacement
{
    /// <summary>
    /// The dialog's own rectangle, moved so its centre meets the work area's. Its SIZE is carried
    /// through untouched: a dialog lays itself out for its own content, and resizing one is how
    /// text gets clipped and buttons end up past the edge.
    /// </summary>
    public static Rectangle Centre(Rectangle dialog, Rectangle workArea)
    {
        // Clamped rather than centred when the dialog does not fit. Centring a 2000px dialog on a
        // 1920px work area puts its left edge at -40, taking the title bar and the close button off
        // screen with it -- and a modal the user cannot dismiss is worse than one badly placed.
        var left = workArea.Left + Math.Max(0, (workArea.Width - dialog.Width) / 2);
        var top = workArea.Top + Math.Max(0, (workArea.Height - dialog.Height) / 2);

        return Rectangle.FromSize(left, top, dialog.Width, dialog.Height);
    }

    /// <summary>
    /// Where a floating dialog goes when the move chord names a direction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A dialog has no tile to travel between, so the chord SNAPS instead of walking a tree: left and
    /// right take a half of the work area, up takes all of it, and down returns the dialog to the
    /// size it opened at, centred.
    /// </para>
    /// <para>
    /// <paramref name="opened"/> is the rectangle the dialog arrived with, and only its SIZE is read.
    /// Down deliberately does not re-centre the CURRENT size: after a snap that size is half the
    /// screen, and centring it would leave a window bearing no relation to the one the application
    /// laid out for its own content.
    /// </para>
    /// </remarks>
    public static Rectangle Snap(Rectangle dialog, Rectangle workArea, Direction direction, Rectangle opened)
    {
        // The right half takes the REMAINDER rather than the same half as the left. On an odd width
        // two equal halves leave a one-pixel seam the user can see through, and rounding both up
        // makes them overlap instead.
        var half = workArea.Width / 2;

        return direction switch
        {
            Direction.Left => Rectangle.FromSize(workArea.Left, workArea.Top, half, workArea.Height),
            Direction.Right => Rectangle.FromSize(
                workArea.Left + half, workArea.Top, workArea.Width - half, workArea.Height),
            Direction.Up => workArea,
            Direction.Down => Centre(opened, workArea),
            _ => dialog,
        };
    }
}
