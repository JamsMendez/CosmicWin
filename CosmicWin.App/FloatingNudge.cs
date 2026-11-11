using CosmicWin.Interop;
using CosmicWin.Layout;

namespace CosmicWin.App;

/// <summary>
/// The nudge chord's arithmetic: one fixed step in a direction, size untouched.
/// </summary>
/// <remarks>
/// Not clamped to the work area, on purpose. Windows itself lets a window be dragged partly off
/// screen, and a nudge that stopped short of the edge would make the last step a different size
/// from every other one.
/// </remarks>
public static class FloatingNudge
{
    public const int Step = 10;

    public static Rectangle Apply(Rectangle window, Direction direction)
    {
        var (dx, dy) = direction switch
        {
            Direction.Left => (-Step, 0),
            Direction.Right => (Step, 0),
            Direction.Up => (0, -Step),
            Direction.Down => (0, Step),
            _ => (0, 0),
        };

        return new Rectangle(window.Left + dx, window.Top + dy, window.Right + dx, window.Bottom + dy);
    }
}
