using CosmicWin.Interop.Win32.VirtualDesktops;
using Xunit.Abstractions;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// Settles the question the per-desktop tree design hangs on: can CosmicWin tell which virtual
/// desktop ANOTHER process's window is on?
/// </summary>
/// <remarks>
/// It matters because the sibling call on the same interface cannot. <c>MoveWindowToDesktop</c> was
/// measured returning <c>E_ACCESSDENIED</c> for a window the caller does not own, so "documented"
/// plainly does not imply "works cross-process" here. If <c>GetWindowDesktopId</c> shares that
/// restriction, a tree per desktop cannot be keyed by asking the shell where each window lives, and
/// the design has to change before it is written rather than after.
/// </remarks>
[Collection(RealDesktopCollection.Name)]
public sealed class WindowDesktopIdDiagnostic(ITestOutputHelper output)
{
    [RequiresDesktopFact]
    public void ReportWhetherAnotherProcessesWindowRevealsItsDesktop()
    {
        var desktops = new Win32VirtualDesktopService();
        output.WriteLine($"supported : {desktops.IsSupported}");
        if (!desktops.IsSupported)
        {
            return;
        }

        using var spawned = SpawnedAlacrittyWindow.Spawn();
        Thread.Sleep(600);

        var current = Win32VirtualDesktopQueries.GetCurrentDesktopId();
        var found = Win32VirtualDesktopQueries.TryGetWindowDesktopId(spawned.Handle, out var windowDesktop, out var error);

        output.WriteLine($"spawned   : 0x{spawned.Handle:X} (pid {spawned.ProcessId}, another process)");
        output.WriteLine($"current   : {current}");
        output.WriteLine($"window on : {(found ? windowDesktop.ToString() : "UNKNOWN")}");
        output.WriteLine($"error     : {error ?? "(none)"}");
        output.WriteLine($"verdict   : {(found && windowDesktop == current ? "USABLE -- reports the desktop it is really on" : "NOT USABLE for keying trees")}");
    }
}
