using CosmicWin.Interop.Win32;
using Xunit.Abstractions;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// Spike, not a diagnostic and not a test: unlike every other file of this shape here, it CHANGES
/// something on the real desktop -- the accent border colour of windows this process does not own.
/// </summary>
/// <remarks>
/// <para>
/// It answers one question that cannot be answered by reading documentation: does
/// <c>DwmSetWindowAttribute(DWMWA_BORDER_COLOR)</c> work CROSS-PROCESS? A window manager owns none
/// of the windows it manages, and this codebase has already been bitten once by assuming otherwise
/// -- <c>IVirtualDesktopManager.MoveWindowToDesktop</c> is documented, takes any HWND, and answers
/// <c>E_ACCESSDENIED</c> for a window the caller does not own.
/// </para>
/// <para>
/// The change is cosmetic and reversible: run with <c>COSMICWIN_SPIKE_BORDER=restore</c> to hand
/// every border back to DWM's default.
/// </para>
/// </remarks>
public sealed class WindowBorderColourSpike(ITestOutputHelper output)
{
    [Fact]
    public void SetTheBorderColourOfEveryTrackableWindowAndReportWhatTheShellSaid()
    {
        if (Environment.GetEnvironmentVariable("COSMICWIN_RUN_DESKTOP_TESTS") != "1")
        {
            output.WriteLine("NOT RUN. Set COSMICWIN_RUN_DESKTOP_TESTS=1 in an interactive desktop session.");
            return;
        }

        var restoring = Environment.GetEnvironmentVariable("COSMICWIN_SPIKE_BORDER") == "restore";

        // A colour nobody would mistake for an accent, so "did it work" needs no squinting.
        var colour = restoring ? Win32WindowBorder.Default : Win32WindowBorder.ColorRef(255, 0, 0);
        output.WriteLine(restoring ? "RESTORING every border to DWM's default." : "Painting every border RED.");

        var source = new Win32NativeWindowSource();
        var applied = 0;
        var refused = 0;

        foreach (var hwnd in source.EnumerateTopLevelWindows())
        {
            if (!source.TryGetWindowInfo(hwnd, out var info) || string.IsNullOrWhiteSpace(info.Title))
            {
                continue;
            }

            var hresult = Win32WindowBorder.TrySetColor(hwnd, colour);
            if (hresult >= 0)
            {
                applied++;
            }
            else
            {
                refused++;
            }

            output.WriteLine(
                $"  0x{hwnd:X8} hresult=0x{hresult:X8} {(hresult >= 0 ? "OK     " : "REFUSED")} " +
                $"proc={info.ProcessName} class={info.ClassName} title={info.Title}");
        }

        output.WriteLine(string.Empty);
        output.WriteLine($"{applied} accepted, {refused} refused.");
        output.WriteLine(
            refused == 0 && applied > 0
                ? "Cross-process border colour WORKS on this build."
                : "At least one window refused -- read the HRESULTs above before building on this.");
    }
}
