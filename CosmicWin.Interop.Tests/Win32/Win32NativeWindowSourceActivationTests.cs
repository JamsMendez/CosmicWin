using CosmicWin.Interop.Win32;
using Windows.Win32;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// MR-2's terminal fact (Engram discovery #106). The fourth supervised run recorded 40 focus chords
/// and <b>every single activation failed</b> — once the focus model stopped drifting, the
/// <c>AttachThreadInput</c> fix from <c>4003d98</c> was revealed to have never worked at all. The
/// cause is that the attach ran on <c>ActionDispatcher.RunAsync</c>'s thread-pool thread, which has
/// no message queue; <c>AttachThreadInput</c> shares INPUT queues, so there was nothing to attach.
/// These facts assert the only property that matters and that no unit test can fake: after
/// activation, the OS itself reports the target as foreground.
/// </summary>
[Trait("Category", "RequiresDesktop")]
[Collection(RealDesktopCollection.Name)]
public sealed class Win32NativeWindowSourceActivationTests
{
    private static nint Foreground() => PInvoke.GetForegroundWindow();

    /// <summary>
    /// Turns a refusal into a diagnosis instead of a mystery. Measured 2026-08-22: with a
    /// full-screen NVIDIA overlay on the desktop these facts fail against a byte-identical
    /// assembly, because a full-screen exclusive window is one of the documented conditions under
    /// which Windows refuses SetForegroundWindow outright. Close any full-screen overlay
    /// (GeForce/game bar) or unlock the session and re-run before suspecting the code.
    /// </summary>
    private static string ForegroundHint() =>
        $"the OS foreground is 0x{Foreground():X}; if this suite fails wholesale, check for a " +
        "full-screen overlay (NVIDIA/GeForce, game bar) or a locked session -- Windows refuses " +
        "every foreground change while one is up, and the assembly under test is then irrelevant";

    [RequiresDesktopFact]
    public void Activate_MovesTheRealOsForegroundToTheTarget_FromAnotherProcessesWindow()
    {
        using var first = SpawnedAlacrittyWindow.Spawn();
        using var second = SpawnedAlacrittyWindow.Spawn();
        var source = new Win32NativeWindowSource();

        // `second` spawned last and is the likely foreground; activate `first` so the call has real
        // work to do rather than short-circuiting on AlreadyForeground.
        var outcome = source.Activate(first.Handle);

        Assert.True(outcome != ActivationOutcome.Failed, $"Activation was refused -- {ForegroundHint()}");
        Assert.NotEqual(ActivationOutcome.AlreadyForeground, outcome);
        Assert.Equal(first.Handle, Foreground());
    }

    /// <summary>
    /// Focus navigation is only useful if it CHAINS: a tiling WM moves focus repeatedly, so one
    /// success followed by a permanent failure would still leave the product broken.
    /// </summary>
    [RequiresDesktopFact]
    public void Activate_ChainsBackAndForth_BetweenTwoRealWindows()
    {
        using var first = SpawnedAlacrittyWindow.Spawn();
        using var second = SpawnedAlacrittyWindow.Spawn();
        var source = new Win32NativeWindowSource();

        Assert.True(source.Activate(first.Handle) != ActivationOutcome.Failed, $"Activation was refused -- {ForegroundHint()}");
        Assert.Equal(first.Handle, Foreground());

        Assert.NotEqual(ActivationOutcome.Failed, source.Activate(second.Handle));
        Assert.Equal(second.Handle, Foreground());

        Assert.NotEqual(ActivationOutcome.Failed, source.Activate(first.Handle));
        Assert.Equal(first.Handle, Foreground());
    }

    /// <summary>The cheap rung: a target that already holds the foreground costs nothing and never escalates.</summary>
    [RequiresDesktopFact]
    public void Activate_WhenTheTargetAlreadyHoldsTheForeground_ShortCircuits()
    {
        using var window = SpawnedAlacrittyWindow.Spawn();
        var source = new Win32NativeWindowSource();
        source.Activate(window.Handle);

        var outcome = source.Activate(window.Handle);

        Assert.True(outcome == ActivationOutcome.AlreadyForeground, $"Expected AlreadyForeground -- {ForegroundHint()}");
    }

    /// <summary><c>TryActivateWindow</c> stays the boolean <see cref="INativeWindowSource"/> contract, now backed by the escalating implementation.</summary>
    [RequiresDesktopFact]
    public void TryActivateWindow_ReportsTrue_WhenTheForegroundGenuinelyMoved()
    {
        using var first = SpawnedAlacrittyWindow.Spawn();
        using var second = SpawnedAlacrittyWindow.Spawn();
        INativeWindowSource source = new Win32NativeWindowSource();

        var activated = source.TryActivateWindow(first.Handle);

        Assert.True(activated, $"Activation was refused -- {ForegroundHint()}");
        Assert.Equal(first.Handle, Foreground());
    }
}
