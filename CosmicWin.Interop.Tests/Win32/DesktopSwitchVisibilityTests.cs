using CosmicWin.Interop.Win32;
using CosmicWin.Interop.Win32.VirtualDesktops;
using Xunit.Abstractions;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// Measures the one fact <see cref="Win32Workspace.Poll"/> now stakes a removal on: a window left
/// behind on another virtual desktop is CLOAKED, and cloaking does not clear <c>WS_VISIBLE</c>.
/// </summary>
/// <remarks>
/// <para>
/// Poll used to keep any window that still answered <c>TryGetWindowInfo</c>, which is what stopped
/// a desktop switch from dismantling the whole layout -- and is also what let a window hidden into
/// the notification area keep its tile forever, because a hidden window is alive too. Separating
/// them requires a signal that says which of the two happened, and this fact is the reason
/// <c>IsVisible</c> can be that signal.
/// </para>
/// <para>
/// Assumed, it would be the exact shape of the regression this project already paid for once. So it
/// is triggered for real -- <c>Win32NativeWindowSourceCloakingTests</c> says DWM cloaking cannot be
/// self-triggered by a spawned window, which was true before this project could switch desktops on
/// purpose and is not any more.
/// </para>
/// <para>Self-cleaning: the desktop the user was on is restored whether the assertions pass or not.</para>
/// </remarks>
[Trait("Category", "RequiresDesktop")]
[Collection(RealDesktopCollection.Name)]
public sealed class DesktopSwitchVisibilityTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(900);

    [RequiresDesktopFact]
    public void AWindowLeftOnAnotherDesktop_StopsBeingEnumerable_ButStaysVisible()
    {
        Thread.Sleep(Settle);

        var desktops = new Win32VirtualDesktopService();
        if (!desktops.IsSupported)
        {
            Assert.Fail($"Unsupported build, so nothing here can be measured: {desktops.LastError}");
        }

        if (desktops.Count < 2)
        {
            Assert.Fail("Needs at least two virtual desktops to leave a window behind on one of them.");
        }

        var source = new Win32NativeWindowSource();
        using var spawned = SpawnedAlacrittyWindow.Spawn();
        Thread.Sleep(Settle);

        Assert.Contains(spawned.Handle, source.EnumerateTopLevelWindows());
        Assert.True(source.TryGetWindowInfo(spawned.Handle, out var before));
        Assert.True(before.IsVisible, "The spawned window was not visible before the switch.");

        var startedOn = desktops.CurrentIndex;
        var elsewhere = startedOn == 1 ? 2 : 1;

        try
        {
            Assert.True(
                desktops.TrySwitchTo(elsewhere),
                $"Could not switch to desktop {elsewhere}, so nothing could be measured: {desktops.LastError}");
            Thread.Sleep(Settle);

            var stillEnumerable = source.EnumerateTopLevelWindows().Contains(spawned.Handle);
            var stillAlive = source.TryGetWindowInfo(spawned.Handle, out var after);

            output.WriteLine(
                $"window 0x{spawned.Handle:X} left on desktop {startedOn}, viewing {elsewhere}: " +
                $"enumerable={stillEnumerable} alive={stillAlive} visible={after.IsVisible}");

            Assert.False(stillEnumerable, "A cloaked window was still enumerable, so this measures nothing.");
            Assert.True(stillAlive, "The window stopped existing merely because the user looked elsewhere.");

            // The whole point. If cloaking cleared WS_VISIBLE, Poll's new removal test could not
            // tell a desktop switch from a close, and every switch would dismantle the layout.
            Assert.True(after.IsVisible, "Cloaking cleared WS_VISIBLE -- Poll cannot use it to detect a hide.");
        }
        finally
        {
            desktops.TrySwitchTo(startedOn);
            Thread.Sleep(Settle);
        }
    }
}
