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
internal static class TreeArranger
{
    public static void ArrangeAndPosition(ITilingEngine engine, WindowRegistry registry, Rect workArea)
    {
        foreach (var (windowRef, bounds) in engine.Arrange(workArea))
        {
            if (!registry.TryGetWindow(windowRef.Handle, out var window) || window is not { IsAlive: true })
            {
                continue;
            }

            window.SetPosition(Rectangle.FromSize(bounds.X, bounds.Y, bounds.Width, bounds.Height));
        }
    }
}
