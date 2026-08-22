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

    [RequiresDesktopFact]
    public void Activate_MovesTheRealOsForegroundToTheTarget_FromAnotherProcessesWindow()
    {
        using var first = SpawnedAlacrittyWindow.Spawn();
        using var second = SpawnedAlacrittyWindow.Spawn();
        var source = new Win32NativeWindowSource();

        // `second` spawned last and is the likely foreground; activate `first` so the call has real
        // work to do rather than short-circuiting on AlreadyForeground.
        var outcome = source.Activate(first.Handle);

        Assert.NotEqual(ActivationOutcome.Failed, outcome);
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

        Assert.NotEqual(ActivationOutcome.Failed, source.Activate(first.Handle));
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

        Assert.Equal(ActivationOutcome.AlreadyForeground, outcome);
    }

    /// <summary><c>TryActivateWindow</c> stays the boolean <see cref="INativeWindowSource"/> contract, now backed by the escalating implementation.</summary>
    [RequiresDesktopFact]
    public void TryActivateWindow_ReportsTrue_WhenTheForegroundGenuinelyMoved()
    {
        using var first = SpawnedAlacrittyWindow.Spawn();
        using var second = SpawnedAlacrittyWindow.Spawn();
        INativeWindowSource source = new Win32NativeWindowSource();

        var activated = source.TryActivateWindow(first.Handle);

        Assert.True(activated);
        Assert.Equal(first.Handle, Foreground());
    }
}
