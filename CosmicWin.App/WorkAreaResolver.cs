using CosmicWin.Interop;
using CosmicWin.Layout;

namespace CosmicWin.App;

/// <summary>
/// Converts a display's real (unscaled) pixel work area into <see cref="Rect"/>, the Win32-free
/// geometry type <see cref="ActionExecutor.WorkArea"/> and <see cref="ITilingEngine.Arrange"/>
/// consume. Pure -- takes an already-resolved <see cref="IDisplay"/>, so it needs no live desktop
/// to unit test (verify-report #21 CRITICAL C1: nothing in production ever supplied a non-zero
/// work area before this existed, so the first Move/Toggle/Resize chord zeroed every window).
/// </summary>
public static class WorkAreaResolver
{
    public static Rect Resolve(IDisplay display)
    {
        var workArea = display.WorkArea;
        return new Rect(workArea.Left, workArea.Top, workArea.Width, workArea.Height);
    }
}
