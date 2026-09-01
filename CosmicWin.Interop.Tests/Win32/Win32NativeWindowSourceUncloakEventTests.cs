using CosmicWin.Interop.Win32;
using CosmicWin.Interop.Win32.VirtualDesktops;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// Reported: Brave and the Windows Settings app take a second or more to join the tree, where an
/// ordinary window joins at once. Measured with <see cref="SlowAdmissionDiagnostic"/> against a real
/// Settings launch: the window is born CLOAKED and stays cloaked through its own CREATE and SHOW,
/// so <c>IsTrackable</c> refuses it at both -- and <c>EVENT_OBJECT_UNCLOAKED</c> (0x8018), the one
/// event that says it has become trackable, is past the end of the subscribed
/// <c>EVENT_OBJECT_CREATE</c>..<c>EVENT_OBJECT_LOCATIONCHANGE</c> range.
/// </summary>
/// <remarks>
/// <para>
/// Nothing looked again, so admission rode on whatever incidental event happened to be delivered
/// after the uncloak -- and on a run where none was, on the two-second reconciliation tick.
/// </para>
/// <para>
/// Uncloaking is triggered here the deterministic way rather than by racing a UWP launch: leaving a
/// virtual desktop cloaks its windows and returning uncloaks them, which
/// <see cref="DesktopSwitchVisibilityTests"/> already measures. The CLOAK half is deliberately NOT
/// handled anywhere -- treating it as a removal is exactly the regression that once dismantled the
/// whole layout on a desktop switch.
/// </para>
/// </remarks>
[Trait("Category", "RequiresDesktop")]
[Collection(RealDesktopCollection.Name)]
public sealed class Win32NativeWindowSourceUncloakEventTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(900);

    [RequiresDesktopFact]
    public void SubscribeWindowEvents_ReportsUncloaked_WhenACloakedWindowBecomesVisibleAgain()
    {
        var desktops = new Win32VirtualDesktopService();
        if (!desktops.IsSupported)
        {
            Assert.Fail($"Unsupported build, so nothing here can be measured: {desktops.LastError}");
        }

        if (desktops.Count < 2)
        {
            Assert.Fail("Needs at least two virtual desktops to cloak a window by leaving one.");
        }

        var source = new Win32NativeWindowSource();
        using var spawned = SpawnedAlacrittyWindow.Spawn();
        Thread.Sleep(Settle);

        var created = new HashSet<nint>();
        using var subscription = source.SubscribeWindowEvents((kind, hwnd) =>
        {
            if (kind == NativeWindowEventKind.Uncloaked)
            {
                created.Add(hwnd);
            }
        });

        var startedOn = desktops.CurrentIndex;
        var elsewhere = startedOn == 1 ? 2 : 1;
        bool observed;

        try
        {
            Assert.True(
                desktops.TrySwitchTo(elsewhere),
                $"Could not leave desktop {startedOn}, so the window was never cloaked: {desktops.LastError}");
            MessagePump.For(Settle);

            // Everything from the way OUT is noise: only what the return reports is the measurement.
            created.Clear();

            Assert.True(
                desktops.TrySwitchTo(startedOn),
                $"Could not return to desktop {startedOn}: {desktops.LastError}");

            observed = MessagePump.Until(() => created.Contains(spawned.Handle), TimeSpan.FromSeconds(5));
        }
        finally
        {
            desktops.TrySwitchTo(startedOn);
            Thread.Sleep(Settle);
        }

        Assert.True(observed, "Uncloaking the window reported no Uncloaked event, so nothing admits it.");
    }
}
