using Windows.Win32.UI.WindowsAndMessaging;

namespace CosmicWin.Interop.Win32;

/// <summary>
/// The one real decision in the Progman/WorkerW attach sequence: whether Progman itself carries
/// <c>WS_EX_NOREDIRECTIONBITMAP</c> on its <c>GWL_EXSTYLE</c>, which marks the 24H2+ "raised
/// desktop" layout (icons live directly under Progman; the sibling <c>WorkerW</c> the
/// <c>0x052C</c> message creates is not the parent) as opposed to the legacy layout (icons live
/// under a <c>WorkerW</c> sibling of the top-level window that owns <c>SHELLDLL_DefView</c>).
/// </summary>
/// <remarks>
/// Extracted as a pure function of the raw style bits so this one branch is unit-testable without
/// a live desktop -- <see cref="Win32VideoWallpaperHost"/> is where the real
/// <c>GetWindowLong(progman, GWL_EXSTYLE)</c> read happens. Ported from the measured sequence in
/// <c>spikes/AttachSpike/Program.cs</c>; see <c>docs/research/video-wallpaper-feasibility.md</c>
/// §3.6 for why this is the only reliable signal (six other detection approaches failed on this
/// machine).
/// </remarks>
internal static class DesktopLayoutDetector
{
    /// <summary>
    /// <see langword="true"/> when <paramref name="progmanExStyle"/> (Progman's raw
    /// <c>GWL_EXSTYLE</c> value) has the <c>WS_EX_NOREDIRECTIONBITMAP</c> bit set.
    /// </summary>
    public static bool IsRaisedDesktop(uint progmanExStyle) =>
        (progmanExStyle & (uint)WINDOW_EX_STYLE.WS_EX_NOREDIRECTIONBITMAP) != 0;
}
