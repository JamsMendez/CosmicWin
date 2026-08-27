using CosmicWin.Interop;
using CosmicWin.Layout;

namespace CosmicWin.App;

/// <summary>
/// Shared "arrange the tree, then position every live leaf's real window" step: both <see cref="ActionExecutor"/> (after a Move/Toggle/Resize mutation) and
/// <see cref="WorkspaceSessionAdapter"/> (after a window is added or removed) call
/// <see cref="ITilingEngine.Arrange"/> and must apply the result identically -- one
/// <see cref="IWindow.SetPosition"/> per live, tracked leaf, respecting <see
/// cref="IWindow.CanReposition"/>'s no-retry contract. Extracted once so the two call sites can
/// never drift apart.
/// </summary>
/// <remarks>
/// A leaf whose
/// <see cref="IWindow.SetPosition"/> call fails here is evicted from the tree and <see
/// cref="WindowRegistry"/> right at this shared choke point, mirroring an earlier decision's WE-1-style
/// exclusion -- not re-implemented at any individual caller. Wired an equivalent guard into
/// only ONE of <see cref="ITilingEngine"/>'s live call sites
/// (<c>MultiMonitorWorkspaceAdapter.OnWindowBoundsChanged</c>); the very next verification
/// reproduced the identical symptom through <c>OnWindowAdded</c>, which had no guard at all.
/// Guarding here instead of at any one caller covers every current AND future call site by
/// construction, including the currently-dormant <c>TreeManager</c>/<c>WorkspaceSessionAdapter</c>
/// ones that will become live once MM-2/MM-3's hotplug driver or a revived session adapter exist.
/// </remarks>
internal static class TreeArranger
{
    /// <summary>Visible space between neighbouring windows, and between a window and the screen edge.</summary>
    public const int DefaultGap = 8;

    /// <summary>
    /// The single spacing knob, defaulting to OFF. Reported: windows had space on three
    /// sides and none at the top, because Win32's invisible resize border is 7px on left/right/
    /// bottom and 0 on top. Interop now lands a tile exactly where it is asked, so any space on
    /// screen is deliberate -- this value, applied identically on all four sides.
    /// <para>
    /// Zero is the default on purpose. Spacing is presentation, while every geometry fact in the
    /// suite is about the tiling ARITHMETIC -- defaulting this to <see cref="DefaultGap"/> silently
    /// changed the expected rectangle of 27 of them at once. Production opts in explicitly, in
    /// <see cref="AppComposition"/>, where the choice is visible.
    /// </para>
    /// </summary>
    public static int Gap { get; set; }

    /// <summary>
    /// Runs after the reflow has settled, once, however many passes it took.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It exists for the focus border, and it is a PARAMETER rather than a second static knob beside
    /// <see cref="Gap"/> on purpose -- that one is already documented as having raced 27 geometry
    /// facts when a test class assigned it, and a second one would be the same trap with a different
    /// name. Required, not optional, so a new call site cannot silently opt out of it the way the
    /// eviction guard was silently missing from <c>OnWindowAdded</c>.
    /// </para>
    /// <para>
    /// Nothing else can carry this. The border's other two paths both miss a reflow nobody asked
    /// for: <c>ActionExecutor.AfterAction</c> only runs for a chord, and the bounds event never
    /// arrives at all, because this method reaches the window through the SAME <see cref="IWindow"/>
    /// instance the workspace caches -- <c>Win32Window.SetPosition</c> updates <c>Bounds</c> here,
    /// so by the time the WinEvent lands <c>Win32Workspace.UpdateBounds</c> compares the new
    /// rectangle against itself and reports no change. Closing a neighbour, sending one to another
    /// desktop and collapsing a group all left the border a full reconciliation interval behind.
    /// </para>
    /// </remarks>
    public static void ArrangeAndPosition(
        ITilingEngine engine, WindowRegistry registry, Rect workArea,
        Action<IReadOnlyList<nint>>? afterArrange) =>
        ArrangeAndPosition(engine, registry, workArea, Gap, afterArrange);

    /// <summary>
    /// Explicit-spacing overload. Exists so a test can pin gap arithmetic WITHOUT assigning <see
    /// cref="Gap"/>: xUnit runs test classes in parallel, so a class that mutated the static raced
    /// every other class's geometry assertions -- observed as one unrelated fact failing in a full
    /// run and passing in isolation.
    /// </summary>
    public static void ArrangeAndPosition(
        ITilingEngine engine, WindowRegistry registry, Rect workArea, int gap,
        Action<IReadOnlyList<nint>>? afterArrange)
    {
        // Allocated only when someone is listening: the overwhelmingly common reflow has no
        // callback at all, and it should cost exactly what it did before.
        var moved = afterArrange is null ? null : new List<nint>();

        Apply(engine, registry, workArea, gap, moved);

        // AFTER the last pass, not inside it. An eviction re-enters Apply, and a listener told to
        // follow each pass would answer geometry the very next pass is about to replace.
        afterArrange?.Invoke(moved!);
    }

    private static void Apply(
        ITilingEngine engine, WindowRegistry registry, Rect workArea, int gap, List<nint>? moved)
    {
        var evictedAny = false;

        // HALF off the work area and HALF off every tile. Two adjacent windows then contribute half
        // each, and an outer edge contributes the work-area half plus the tile's half, so BOTH
        // distances come to exactly Gap. Taking a whole gap off each tile instead would make the
        // screen edge twice as wide as the space between windows.
        var half = Math.Max(0, gap) / 2;
        var field = Deflate(workArea, half);

        foreach (var (windowRef, bounds) in engine.Arrange(field))
        {
            if (!registry.TryGetWindow(windowRef.Handle, out var window) || window is not { IsAlive: true })
            {
                continue;
            }

            if (window.CanReposition)
            {
                var tile = Deflate(bounds, half);
                var before = window.Bounds;
                window.SetPosition(Rectangle.FromSize(tile.X, tile.Y, tile.Width, tile.Height));

                // Only what actually MOVED. Re-arranging an unchanged tree re-applies the same
                // geometry to every tile -- which is how a drag gets undone -- and reporting all of
                // them would make "the tree was reflowed" indistinguishable from "your window
                // moved", the one question the listener is asking.
                if (window.Bounds != before)
                {
                    moved?.Add(windowRef.Handle);
                }
            }

            // Checked AFTER the attempt above: CanReposition may have just flipped false as a
            // result of THIS call's own SetPosition attempt failing (its documented contract), or
            // it may already have been false coming in -- either way, never retry; evict instead.
            if (!window.CanReposition &&
                registry.TryGetLeaf(windowRef.Handle, out var leaf) && leaf is not null &&
                engine.Remove(leaf))
            {
                registry.Remove(windowRef.Handle);
                evictedAny = true;
            }
        }

        if (evictedAny)
        {
            // Tree shape changed (at least one leaf evicted) -- reflow the survivors into the
            // vacated space. Terminates: each recursive pass strictly shrinks the live-leaf set.
            Apply(engine, registry, workArea, gap, moved);
        }
    }

    /// <summary>
    /// Translates a finished hand-resize of <paramref name="leaf"/> into the tree, so the reflow
    /// that follows lands the size the user dragged instead of snapping it back. Reports whether
    /// anything in the tree actually changed.
    /// </summary>
    /// <remarks>
    /// The comparison has to be against the tile that was APPLIED, not the slot the tree handed
    /// out: <see cref="Apply"/> deflates every slot by half the gap before placing the window, so
    /// measuring the drag against the raw <see cref="Node.LastGeometry"/> would read a whole gap of
    /// phantom movement on a build that has spacing turned on and none on a build that does not.
    /// </remarks>
    public static bool TryApplyUserResize(Node leaf, Rectangle dragged) =>
        TryApplyUserResize(leaf, dragged, Gap);

    /// <summary>Explicit-spacing overload, for the same reason the arrange one has one.</summary>
    public static bool TryApplyUserResize(Node leaf, Rectangle dragged, int gap)
    {
        var placed = Deflate(leaf.LastGeometry, Math.Max(0, gap) / 2);

        // A leaf that has never been arranged has no tile to have been dragged away FROM, so there
        // is no delta to read -- only a rectangle-shaped guess at one.
        if (placed.Width <= 0 || placed.Height <= 0)
        {
            return false;
        }

        return LayoutTree.ApplyEdgeDrag(
            leaf,
            placed,
            new Rect(dragged.Left, dragged.Top, dragged.Width, dragged.Height));
    }

    /// <summary>
    /// Shrinks <paramref name="rect"/> by <paramref name="inset"/> on every side, never past the
    /// point of collapsing: a work area too small for the requested gap keeps its windows visible
    /// rather than sizing them to nothing.
    /// </summary>
    private static Rect Deflate(Rect rect, int inset)
    {
        if (inset <= 0 || rect.Width <= inset * 2 || rect.Height <= inset * 2)
        {
            return rect;
        }

        return new Rect(rect.X + inset, rect.Y + inset, rect.Width - (inset * 2), rect.Height - (inset * 2));
    }
}
