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
/// <para>
/// It demands its OWN opt-in, and that is not symmetry with the other gates -- it is the difference
/// between a fact that reads the desktop and one that repaints it. Gated on the shared desktop
/// opt-in alone, it ran as an ordinary <c>[Fact]</c> on every <c>dotnet test</c> of the SOLUTION and
/// left every window on the machine wearing a red border, with no restore. That is not
/// hypothetical: it happened five times in one session and was reported as a defect in CosmicWin's
/// own focus border. A DWM attribute belongs to the WINDOW, so the paint survives the test run, the
/// testhost process, and a reboot -- nothing takes it back.
/// </para>
/// <para>
/// In the serialised desktop collection like every other fact that touches the real desktop.
/// A class with NO <c>[Collection]</c> gets its own implicit one, which xunit runs in PARALLEL
/// with <c>RealDesktop</c> -- so this raced the very facts that collection exists to serialise.
/// Read-only is not an exemption: a reader that runs while another fact is moving windows or
/// switching desktops reports a desktop nobody ever had.
/// </para>
/// </remarks>
[Trait("Category", "RequiresDesktop")]
[Collection(RealDesktopCollection.Name)]
public sealed class WindowBorderColourSpike(ITestOutputHelper output)
{
    /// <summary>The spike's own opt-in, separate from the shared desktop one (see the remarks).</summary>
    private const string ModeVariable = "COSMICWIN_SPIKE_BORDER";

    [Fact]
    public void SetTheBorderColourOfEveryTrackableWindowAndReportWhatTheShellSaid()
    {
        if (DesktopGate.OptInSkipReason() is { } notRun)
        {
            output.WriteLine($"NOT RUN. {notRun}");
            return;
        }

        // The second opt-in, and the one that matters. Absent, this does NOTHING -- deliberately
        // including the restore, so the variable means "I know what this touches" rather than
        // "which direction". Anyone who needs to undo a painting already has to set it.
        var mode = Environment.GetEnvironmentVariable(ModeVariable);
        if (mode is not ("paint" or "restore"))
        {
            output.WriteLine(
                $"NOT RUN. Set {ModeVariable}=paint to repaint every window's border on this " +
                $"machine, or {ModeVariable}=restore to hand them all back to DWM.");
            return;
        }

        var restoring = mode == "restore";

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
