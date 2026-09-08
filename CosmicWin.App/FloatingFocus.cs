using CosmicWin.Layout;

namespace CosmicWin.App;

/// <summary>
/// Answers a focus chord GEOMETRICALLY, for the mode where there is no tree to walk. Pure and
/// static -- no Win32, no workspace, no executor state -- so the two rules below are unit-testable
/// against plain rectangles, the same way <c>NearestTileToward</c> is testable against a tree.
/// </summary>
/// <remarks>
/// <para>
/// Two passes, tried in order, and only the second is new: AHEAD asks "what is the nearest window
/// actually lying in the direction pressed", the exact centre-distance comparison
/// <c>ActionExecutor.NearestTileToward</c> already uses for tiles -- the two paths must agree, and
/// sharing the comparison shape is how. STACK answers the case a tiled tree never has to: a window
/// evicted from tiling, or one the user dragged there by hand, sitting exactly on top of another.
/// Nothing lies "ahead" of a window directly underneath it, so the only way to reach it at all is to
/// walk the STACK it is piled in.
/// </para>
/// <para>
/// STACK is tried ONLY when AHEAD found nothing, never as a tie-break or a fallback candidate list.
/// Nothing in the direction pressed is a legitimate answer of "no" -- the same rule
/// <c>NearestTileToward</c> already obeys -- so a direction that genuinely has nothing ahead of it
/// must still return null rather than quietly answering from the stack instead.
/// </para>
/// </remarks>
public static class FloatingFocus
{
    /// <summary>
    /// The window <paramref name="direction"/> should move focus to from <paramref name="from"/>,
    /// or <see langword="null"/> when nothing answers.
    /// </summary>
    /// <param name="candidates">
    /// Every window on screen, in Z-ORDER, TOPMOST FIRST. The ordering is load-bearing only for the
    /// STACK pass below -- AHEAD reads every entry regardless of position -- but it is the caller's
    /// contract to uphold, not something this method can verify from a list of rectangles alone.
    /// </param>
    public static nint? Toward(
        Interop.Rectangle from,
        nint fromHandle,
        Direction direction,
        IReadOnlyList<(nint Handle, Interop.Rectangle Bounds)> candidates)
    {
        if (NearestAhead(from, fromHandle, direction, candidates) is { } ahead)
        {
            return ahead;
        }

        return NextInStack(from, fromHandle, candidates);
    }

    /// <summary>
    /// The nearest candidate whose centre lies STRICTLY in <paramref name="direction"/> from
    /// <paramref name="from"/>'s centre, or <see langword="null"/> when nothing does.
    /// </summary>
    /// <remarks>
    /// Ties broken on the smaller PERPENDICULAR centre distance, then on the earlier list position.
    /// Neither tie-break needs its own bookkeeping: a candidate only replaces the current winner on a
    /// STRICT improvement of one or the other, so the first candidate to reach a given (distance,
    /// perpendicular) pair is the one a later, merely-equal candidate can never displace.
    /// </remarks>
    private static nint? NearestAhead(
        Interop.Rectangle from, nint fromHandle, Direction direction,
        IReadOnlyList<(nint Handle, Interop.Rectangle Bounds)> candidates)
    {
        var originX = from.Left + (from.Width / 2);
        var originY = from.Top + (from.Height / 2);

        nint? nearest = null;
        var shortest = long.MaxValue;
        var shortestPerpendicular = long.MaxValue;

        foreach (var (handle, bounds) in candidates)
        {
            if (handle == fromHandle || bounds.Width <= 0 || bounds.Height <= 0)
            {
                continue;
            }

            var x = bounds.Left + (bounds.Width / 2);
            var y = bounds.Top + (bounds.Height / 2);

            var distance = direction switch
            {
                Direction.Left => originX - x,
                Direction.Right => x - originX,
                Direction.Up => originY - y,
                _ => y - originY,
            };

            if (distance <= 0)
            {
                continue;
            }

            var perpendicular = direction is Direction.Left or Direction.Right
                ? Math.Abs(y - originY)
                : Math.Abs(x - originX);

            if (distance < shortest || (distance == shortest && perpendicular < shortestPerpendicular))
            {
                shortest = distance;
                shortestPerpendicular = perpendicular;
                nearest = handle;
            }
        }

        return nearest;
    }

    /// <summary>
    /// The next window BEHIND <paramref name="fromHandle"/>, in z-order, whose rectangle overlaps
    /// <paramref name="from"/> -- what makes a repeated press walk down through windows piled in the
    /// same place instead of bouncing off the topmost one forever.
    /// </summary>
    /// <remarks>
    /// Searches strictly AFTER <paramref name="fromHandle"/>'s own position in
    /// <paramref name="candidates"/>, never before it: those earlier entries are ON TOP of the
    /// origin, and stepping onto one of those would move focus BACKWARD through the pile the moment
    /// the origin is not the topmost window in it. When <paramref name="fromHandle"/> is not in the
    /// list at all -- the origin resolved through <see cref="ActionExecutor.ResolveWindowBounds"/>
    /// but did not survive whatever filter built <paramref name="candidates"/> -- there is no
    /// position to search after, so the first overlapping candidate answers instead.
    /// </remarks>
    private static nint? NextInStack(
        Interop.Rectangle from, nint fromHandle,
        IReadOnlyList<(nint Handle, Interop.Rectangle Bounds)> candidates)
    {
        var fromIndex = -1;
        for (var i = 0; i < candidates.Count; i++)
        {
            if (candidates[i].Handle == fromHandle)
            {
                fromIndex = i;
                break;
            }
        }

        for (var i = 0; i < candidates.Count; i++)
        {
            var (handle, bounds) = candidates[i];
            if (handle == fromHandle || bounds.Width <= 0 || bounds.Height <= 0 || i <= fromIndex)
            {
                continue;
            }

            if (Overlaps(from, bounds))
            {
                return handle;
            }
        }

        return null;
    }

    /// <summary>
    /// STRICT rectangle overlap -- edges merely touching do not count, which matches every other
    /// geometry test in this codebase (see <c>Rectangle.Contains</c>'s own edge-inclusive contrast).
    /// Two windows sharing only a border are adjacent, not piled on top of one another.
    /// </summary>
    private static bool Overlaps(Interop.Rectangle a, Interop.Rectangle b) =>
        a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;
}
