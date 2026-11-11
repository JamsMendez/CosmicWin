using CosmicWin.Interop;
using CosmicWin.Layout;

namespace CosmicWin.App;

/// <summary>
/// Where the move chord sends a window while tiling is off, Windows-snap style.
/// </summary>
/// <remarks>
/// <para>
/// Left and right take that half of the work area at full height. Up and down take the top or
/// bottom half at full width -- unless another window already holds that half, in which case the
/// two share it as quarters, the one already there on the left. The half holds two windows at
/// most: a third sent there lands on the right quarter, over the window already in it.
/// </para>
/// <para>
/// Only windows THIS snapper placed are counted as holding a half, and only while they are still
/// exactly where it put them, on the same work area and virtual desktop. A window the user dragged
/// away, minimised or closed frees its slot without anything having to tell this class about it.
/// </para>
/// <para>
/// Spacing follows the tiled layout's own arithmetic (<see cref="TreeArranger"/>): half the gap is
/// taken off the work area and half off every slot, so the screen edge and the seam between two
/// windows are both exactly one gap wide.
/// </para>
/// </remarks>
public sealed class FloatingSnapper(Func<int> gap)
{
    public enum Slot
    {
        Left,
        Right,
        Top,
        TopLeft,
        TopRight,
        Bottom,
        BottomLeft,
        BottomRight,
    }

    private sealed record Placement(IWindow Window, Slot Slot, Rectangle WorkArea, Guid Desktop, Rectangle Landed);

    private readonly Dictionary<nint, Placement> _placed = [];

    public void Snap(IWindow window, Rectangle workArea, Direction direction, Guid desktop)
    {
        Forget(entry => !entry.Window.IsAlive || entry.Window.Bounds != entry.Landed);

        switch (direction)
        {
            case Direction.Left:
                Place(window, Slot.Left, workArea, desktop);
                break;
            case Direction.Right:
                Place(window, Slot.Right, workArea, desktop);
                break;
            case Direction.Up:
                SnapIntoBand(window, workArea, desktop, Slot.Top, Slot.TopLeft, Slot.TopRight);
                break;
            case Direction.Down:
                SnapIntoBand(window, workArea, desktop, Slot.Bottom, Slot.BottomLeft, Slot.BottomRight);
                break;
        }
    }

    private void SnapIntoBand(
        IWindow window, Rectangle workArea, Guid desktop, Slot whole, Slot left, Slot right)
    {
        var occupants = _placed.Values
            .Where(entry => entry.Window.Handle != window.Handle
                && entry.WorkArea == workArea
                && entry.Desktop == desktop
                && (entry.Slot == whole || entry.Slot == left || entry.Slot == right))
            .ToList();

        switch (occupants)
        {
            case []:
                Place(window, whole, workArea, desktop);
                break;
            case [var only] when only.Slot == whole:
                Place(only.Window, left, workArea, desktop);
                Place(window, right, workArea, desktop);
                break;
            case [var only]:
                Place(window, only.Slot == left ? right : left, workArea, desktop);
                break;
            default:
                // Full: the newcomer overlaps the right quarter, and nobody else moves.
                Place(window, right, workArea, desktop);
                break;
        }
    }

    private void Place(IWindow window, Slot slot, Rectangle workArea, Guid desktop)
    {
        window.SetPosition(RectOf(slot, workArea, gap()));

        if (window.IsAlive && window.CanReposition)
        {
            _placed[window.Handle] = new Placement(window, slot, workArea, desktop, window.Bounds);
        }
        else
        {
            _placed.Remove(window.Handle);
        }
    }

    private void Forget(Func<Placement, bool> stale)
    {
        foreach (var handle in _placed.Where(entry => stale(entry.Value)).Select(entry => entry.Key).ToArray())
        {
            _placed.Remove(handle);
        }
    }

    /// <summary>The rectangle a slot covers on <paramref name="workArea"/>, gaps included.</summary>
    public static Rectangle RectOf(Slot slot, Rectangle workArea, int gap)
    {
        var half = Math.Max(0, gap) / 2;
        var field = Deflate(workArea, half);

        // The right and bottom halves take the REMAINDER, as the tiled layout does, so an odd
        // length leaves neither a seam nor an overlap.
        var halfWidth = field.Width / 2;
        var halfHeight = field.Height / 2;
        var midX = field.Left + halfWidth;
        var midY = field.Top + halfHeight;

        var region = slot switch
        {
            Slot.Left => field with { Right = midX },
            Slot.Right => field with { Left = midX },
            Slot.Top => field with { Bottom = midY },
            Slot.TopLeft => new Rectangle(field.Left, field.Top, midX, midY),
            Slot.TopRight => new Rectangle(midX, field.Top, field.Right, midY),
            Slot.Bottom => field with { Top = midY },
            Slot.BottomLeft => new Rectangle(field.Left, midY, midX, field.Bottom),
            Slot.BottomRight => new Rectangle(midX, midY, field.Right, field.Bottom),
            _ => field,
        };

        return Deflate(region, half);
    }

    private static Rectangle Deflate(Rectangle rect, int inset)
    {
        if (inset <= 0 || rect.Width <= inset * 2 || rect.Height <= inset * 2)
        {
            return rect;
        }

        return new Rectangle(rect.Left + inset, rect.Top + inset, rect.Right - inset, rect.Bottom - inset);
    }
}
