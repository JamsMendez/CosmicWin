using CosmicWin.Interop.Win32.VirtualDesktops;
using Xunit.Abstractions;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// Proves a window belonging to ANOTHER process can actually be moved between desktops.
/// </summary>
/// <remarks>
/// <para>
/// This is the fact the whole feature turns on, and the obvious route was already measured failing:
/// the DOCUMENTED <c>IVirtualDesktopManager.MoveWindowToDesktop</c> returns <c>E_ACCESSDENIED</c>
/// for a window the caller does not own, and a window manager owns none of the windows it manages.
/// The replacement resolves the HWND through <c>IApplicationViewCollection</c> and goes through
/// <c>MoveViewToDesktop</c> on the internal manager -- one more undocumented interface, so it gets
/// the same treatment: verified end to end against a real window before being trusted.
/// </para>
/// <para>
/// Verified by OUTCOME, not by return value. The move is confirmed with
/// <c>GetWindowDesktopId</c> -- a different interface entirely -- because "the call did not throw"
/// has already proven worthless twice in this project, once for <c>SetForegroundWindow</c> and once
/// for <c>SwitchDesktop</c>.
/// </para>
/// <para>Self-cleaning: whatever it creates or moves is put back, and the assertions run first.</para>
/// </remarks>
[Collection(RealDesktopCollection.Name)]
public sealed class VirtualDesktopMoveTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(900);

    [RequiresDesktopFact]
    public void AnotherProcessesWindow_CanBeMovedToAnotherDesktop_AndBack()
    {
        Thread.Sleep(Settle);

        var desktops = new Win32VirtualDesktopService();
        if (!desktops.IsSupported)
        {
            Assert.Fail($"Unsupported build, so nothing here can be measured: {desktops.LastError}");
        }

        using var spawned = SpawnedAlacrittyWindow.Spawn();
        Thread.Sleep(Settle);

        var startedOn = desktops.CurrentIndex;
        var startedWith = desktops.Count;
        Assert.True(
            Win32VirtualDesktopQueries.TryGetWindowDesktopId(spawned.Handle, out var homeDesktop, out var readError),
            $"Could not read the spawned window's desktop, so a move could not be verified: {readError}");

        var target = startedOn == 1 ? 2 : 1;
        output.WriteLine($"window 0x{spawned.Handle:X} starts on desktop {startedOn} ({homeDesktop}); moving to {target}");

        try
        {
            var moved = desktops.TryMoveWindowTo(spawned.Handle, target);
            Thread.Sleep(Settle);

            Assert.True(moved, $"TryMoveWindowTo reported failure: {desktops.LastError}");

            Assert.True(
                Win32VirtualDesktopQueries.TryGetWindowDesktopId(spawned.Handle, out var nowOn, out var afterError),
                $"Could not re-read the window's desktop after the move: {afterError}");

            output.WriteLine($"after move : {nowOn}");
            Assert.NotEqual(homeDesktop, nowOn);

            // And the user was NOT dragged along: sending a window away and following it are
            // separate intents, and only one of them was asked for.
            Assert.Equal(startedOn, desktops.CurrentIndex);
        }
        finally
        {
            desktops.TryMoveWindowTo(spawned.Handle, startedOn);
            Thread.Sleep(Settle);
            output.WriteLine($"restored   : count={desktops.Count} (started with {startedWith})");
        }
    }
}
