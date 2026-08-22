using CosmicWin.Interop.Win32.VirtualDesktops;
using Xunit.Abstractions;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// Proves the declared virtual-desktop vtable matches this Windows build against MORE THAN ONE
/// desktop.
/// </summary>
/// <remarks>
/// <para>
/// The first probe run on this machine passed with a single desktop, which is weak evidence: with
/// one entry, "GetCount is plausible", "GetDesktops yields that many" and "the current id is among
/// them" are all easy to satisfy by accident, so a shifted vtable could slip through. Two or more
/// desktops make the three slots corroborate each other for real.
/// </para>
/// <para>
/// The extra desktop is arranged through <see cref="ShellDesktopShortcuts"/> -- Windows' own
/// documented <c>Win+Ctrl+D</c> -- deliberately NOT through <c>CreateDesktop()</c>. Calling an
/// unverified mutator to verify itself would be circular, and calling one through a mismatched
/// vtable is precisely the failure this whole design exists to avoid.
/// </para>
/// <para>
/// Self-cleaning: the desktop it creates is closed again, and the assertions run before that
/// cleanup so a failure still leaves the machine as it was found.
/// </para>
/// </remarks>
[Collection(RealDesktopCollection.Name)]
public sealed class VirtualDesktopVTableTests(ITestOutputHelper output)
{
    /// <summary>The shell animates a desktop switch; reading mid-animation is not meaningful.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(900);

    [RequiresDesktopFact]
    public void TheDeclaredVTableStillAgreesWithItself_AcrossMoreThanOneDesktop()
    {
        // Settle BEFORE the baseline, not only between steps. Every desktop-mutating fact in this
        // collection leaves the shell mid-animation, and a baseline read from that state made this
        // test fail twice in full runs while passing in isolation -- test coupling, not a defect.
        Thread.Sleep(Settle);

        var before = VirtualDesktopProbe.Run();
        output.WriteLine($"before : supported={before.Supported} count={before.Count} current={before.CurrentDesktopId}");

        Assert.True(
            before.Supported,
            $"Baseline probe already failed, so nothing below would mean anything: {before.Failure}");

        // Verify the SETUP took effect before asserting on what it was supposed to prove. This step
        // rides on synthetic Win+Ctrl+D, which the shell delivers through the foreground -- and the
        // other real-desktop facts churn the foreground constantly. This test failed intermittently
        // in full runs while passing in isolation, and the cause was never established; what IS
        // certain is that a lost keystroke would surface as "the vtable disagrees", blaming the
        // product for an input that never arrived.
        var created = false;
        for (var attempt = 0; attempt < 3 && !created; attempt++)
        {
            ShellDesktopShortcuts.SendCreateDesktop();
            Thread.Sleep(Settle);
            created = VirtualDesktopProbe.Run().Count == before.Count + 1;
        }

        Assert.True(
            created,
            "Setup failed, not the subject under test: synthetic Win+Ctrl+D never added a desktop " +
            "after three attempts, so there was no second desktop to cross-check the vtable against.");

        VirtualDesktopProbeResult withExtra;
        try
        {
            withExtra = VirtualDesktopProbe.Run();
            output.WriteLine($"with + : supported={withExtra.Supported} count={withExtra.Count} current={withExtra.CurrentDesktopId}");
            output.WriteLine($"         ids={string.Join(", ", withExtra.EnumeratedIds)}");
        }
        finally
        {
            ShellDesktopShortcuts.SendCloseDesktop();
            Thread.Sleep(Settle);
        }

        var after = VirtualDesktopProbe.Run();
        output.WriteLine($"after  : supported={after.Supported} count={after.Count} current={after.CurrentDesktopId}");

        // The real evidence: every cross-check still holds with the set genuinely larger, the count
        // moved by exactly one, and the shell put us on the NEW desktop -- three slots agreeing
        // about a state that did not exist a moment ago.
        Assert.True(withExtra.Supported, $"The vtable stopped agreeing once a second desktop existed: {withExtra.Failure}");
        Assert.Equal(before.Count + 1, withExtra.Count);
        Assert.Equal(withExtra.Count, withExtra.EnumeratedIds.Count);
        Assert.Contains(before.CurrentDesktopId, withExtra.EnumeratedIds);
        Assert.NotEqual(before.CurrentDesktopId, withExtra.CurrentDesktopId);

        // And the machine is left as it was found.
        Assert.Equal(before.Count, after.Count);
        Assert.Equal(before.CurrentDesktopId, after.CurrentDesktopId);
    }
}
