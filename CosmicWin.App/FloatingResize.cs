using CosmicWin.Interop;
using CosmicWin.Layout;

namespace CosmicWin.App;

/// <summary>
/// What a resize chord means for a window that is in no tree: with tiling switched off, every
/// window on the desktop.
/// </summary>
/// <remarks>
/// <para>
/// The rule is <see cref="LayoutTree.ResizeNode"/>'s, read against the work area instead of against
/// a group. That one grows into the neighbour on the pressed side when there is one, and otherwise
/// pushes the OPPOSITE boundary the same way, which shrinks. Out here there are no neighbours, so
/// the work-area edge plays the part of "nobody there": the pressed edge moves outward while there
/// is room, and once it is against the edge the opposite one comes in instead.
/// </para>
/// <para>
/// Mirroring rather than inventing is the whole point. The tiling switch exists to stop CosmicWin
/// laying windows out, not to make its chords mean something else -- a Ctrl+Alt+L that grew a tile
/// rightwards in one mode and did some unrelated thing in the other would be two features wearing
/// one keybinding.
/// </para>
/// <para>
/// Pure arithmetic with no Win32 in it, kept beside <see cref="DialogPlacement"/> for the same
/// reason that one is: a window this touches is never in the tree, so it has no siblings to divide
/// a region with and nothing to reflow.
/// </para>
/// </remarks>
public static class FloatingResize
{
    /// <summary>
    /// Where <paramref name="window"/> goes when the resize chord names
    /// <paramref name="direction"/>, or <see langword="null"/> when it cannot go anywhere.
    /// </summary>
    /// <param name="step">
    /// The fraction of the WORK AREA one press transfers -- the work area standing in for the
    /// ancestor group whose length the tiled resize takes its step from, so one press moves a
    /// comparable distance in both modes.
    /// </param>
    /// <param name="limits">
    /// The sizes this window has demonstrated it will not go under or over, when they are known.
    /// Read for the same reason the tiled path reads them: a chord that asks for a size the window
    /// refuses produces a rectangle nobody ever occupies, and the user presses again.
    /// </param>
    /// <remarks>
    /// <see langword="null"/> is a real answer rather than a failure -- a window already at its own
    /// floor has nowhere to go, and saying so lets the caller leave it alone instead of writing
    /// back the rectangle it already has.
    /// </remarks>
    public static Rectangle? Apply(
        Rectangle window,
        Rectangle workArea,
        Direction direction,
        double step = LayoutTree.DefaultResizeStep,
        (int MinWidth, int MinHeight, int MaxWidth, int MaxHeight)? limits = null)
    {
        if (window.Width <= 0 || window.Height <= 0 || workArea.Width <= 0 || workArea.Height <= 0)
        {
            return null;
        }

        var sideways = direction is Direction.Left or Direction.Right;

        // Left and Right resize the width, Up and Down the height -- the same pairing the tiled
        // path uses, because that is the axis ResizeNode's matching ancestor divides.
        var length = sideways ? window.Width : window.Height;
        var span = sideways ? workArea.Width : workArea.Height;

        var floor = sideways ? limits?.MinWidth ?? 0 : limits?.MinHeight ?? 0;
        var ceiling = sideways ? limits?.MaxWidth ?? int.MaxValue : limits?.MaxHeight ?? int.MaxValue;

        // Rounded away from zero exactly as the tiled step is, so a small work area still moves by
        // a whole pixel rather than by nothing at all.
        var requested = (int)Math.Round(span * step, MidpointRounding.AwayFromZero);
        if (requested <= 0)
        {
            return null;
        }

        // How far the pressed edge can travel before it meets the work area. This is the reading of
        // "is there a neighbour on that side": room means grow, no room means shrink.
        var room = direction switch
        {
            Direction.Left => window.Left - workArea.Left,
            Direction.Right => workArea.Right - window.Right,
            Direction.Up => window.Top - workArea.Top,
            _ => workArea.Bottom - window.Bottom,
        };

        // A window already hanging outside the work area is not "growing into" anything, and
        // treating a negative overhang as room would hand the chord a transfer of the wrong sign.
        return room > 0
            ? Grow(window, direction, Math.Min(requested, Math.Min(room, ceiling - length)))
            : Shrink(window, direction, Math.Min(requested, length - floor));
    }

    /// <summary>
    /// The pressed edge moves OUTWARD by <paramref name="by"/>; the opposite one is pinned.
    /// </summary>
    private static Rectangle? Grow(Rectangle window, Direction direction, int by)
    {
        if (by <= 0)
        {
            // Against the work area, or already as big as the window will go. Refused rather than
            // quietly turned into a shrink: the user asked for more room in a direction that has
            // none, and silently taking room away instead would be answering a different chord.
            return null;
        }

        return direction switch
        {
            Direction.Left => Rectangle.FromSize(
                window.Left - by, window.Top, window.Width + by, window.Height),
            Direction.Right => Rectangle.FromSize(
                window.Left, window.Top, window.Width + by, window.Height),
            Direction.Up => Rectangle.FromSize(
                window.Left, window.Top - by, window.Width, window.Height + by),
            _ => Rectangle.FromSize(window.Left, window.Top, window.Width, window.Height + by),
        };
    }

    /// <summary>
    /// The pressed edge is pinned and the OPPOSITE one comes in by <paramref name="by"/> -- the
    /// tiled path's "push the opposite boundary the same way", which is what makes a decremental
    /// resize possible at all for the leading child of a group.
    /// </summary>
    private static Rectangle? Shrink(Rectangle window, Direction direction, int by)
    {
        if (by <= 0)
        {
            // Already at its own floor. The tiled path stops here too, and for the harder-won
            // reason: a chord that asks a window to go under a size it refuses produced a tug of
            // war -- five presses, five rounds, no movement.
            return null;
        }

        return direction switch
        {
            // Pressed left with nothing to the left: the LEFT edge stays and the right comes in.
            Direction.Left => Rectangle.FromSize(
                window.Left, window.Top, window.Width - by, window.Height),
            Direction.Right => Rectangle.FromSize(
                window.Left + by, window.Top, window.Width - by, window.Height),
            Direction.Up => Rectangle.FromSize(
                window.Left, window.Top, window.Width, window.Height - by),
            _ => Rectangle.FromSize(window.Left, window.Top + by, window.Width, window.Height - by),
        };
    }
}
