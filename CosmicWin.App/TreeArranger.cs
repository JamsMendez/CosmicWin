using CosmicWin.Interop;
using CosmicWin.Layout;

namespace CosmicWin.App;

/// <summary>
/// Shared "arrange the tree, then position every live leaf's real window" step (verify-report
/// #21 CRITICAL C2): both <see cref="ActionExecutor"/> (after a Move/Toggle/Resize mutation) and
/// <see cref="WorkspaceSessionAdapter"/> (after a window is added or removed) call
/// <see cref="ITilingEngine.Arrange"/> and must apply the result identically -- one
/// <see cref="IWindow.SetPosition"/> per live, tracked leaf, respecting <see
/// cref="IWindow.CanReposition"/>'s no-retry contract. Extracted once so the two call sites can
/// never drift apart.
/// </summary>
/// <remarks>
/// V22-W1 (verify-report #21 CRITICAL, decision #83's shared-choke-point pattern): a leaf whose
/// <see cref="IWindow.SetPosition"/> call fails here is evicted from the tree and <see
/// cref="WindowRegistry"/> right at this shared choke point, mirroring decision #81's WE-1-style
/// exclusion -- not re-implemented at any individual caller. WU22 wired an equivalent guard into
/// only ONE of <see cref="ITilingEngine"/>'s live call sites
/// (<c>MultiMonitorWorkspaceAdapter.OnWindowBoundsChanged</c>); the very next verification
/// reproduced the identical symptom through <c>OnWindowAdded</c>, which had no guard at all.
/// Guarding here instead of at any one caller covers every current AND future call site by
/// construction, including the currently-dormant <c>TreeManager</c>/<c>WorkspaceSessionAdapter</c>
/// ones that will become live once MM-2/MM-3's hotplug driver or a revived session adapter exist.
/// </remarks>
internal static class TreeArranger
{
    public static void ArrangeAndPosition(ITilingEngine engine, WindowRegistry registry, Rect workArea)
    {
        var evictedAny = false;

        foreach (var (windowRef, bounds) in engine.Arrange(workArea))
        {
            if (!registry.TryGetWindow(windowRef.Handle, out var window) || window is not { IsAlive: true })
            {
                continue;
            }

            if (window.CanReposition)
            {
                window.SetPosition(Rectangle.FromSize(bounds.X, bounds.Y, bounds.Width, bounds.Height));
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
            ArrangeAndPosition(engine, registry, workArea);
        }
    }
}
