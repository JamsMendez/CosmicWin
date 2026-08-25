using CosmicWin.Interop.Win32;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// <c>Activate</c> hands its escalation to a worker thread and bounds the wait. Until now BOTH
/// endings of that wait returned <see cref="ActivationOutcome.Failed"/>, whose own documentation
/// says "every rung was refused" -- a statement the timeout path has no right to make, because on
/// that path no rung ran at all and the worker may never have been scheduled.
/// </summary>
/// <remarks>
/// <para>
/// That collapse is why this repository's desktop flakiness has resisted diagnosis: every red
/// activation could equally have been Windows refusing the foreground or a starved thread, and the
/// evidence to tell them apart was discarded at the return statement.
/// </para>
/// <para>
/// These facts need no desktop and touch no window. The bound is exercised through a seam that takes
/// the attempt as a delegate, so the two endings are forced rather than waited for.
/// </para>
/// </remarks>
public sealed class BoundedActivationTests
{
    /// <summary>
    /// Generous on purpose. It bounds a delegate that returns immediately, so the only way to
    /// exhaust it is a machine frozen for five seconds -- at which point every fact is failing.
    /// The timeout fact below needs no such generosity: its worker blocks forever, so no amount of
    /// scheduling luck can make that wait succeed.
    /// </summary>
    private static readonly TimeSpan LongEnoughForAnImmediateReturn = TimeSpan.FromSeconds(5);

    [Fact]
    public void RunBounded_WhenTheWorkerOutlastsTheBudget_SaysItTimedOutRatherThanRefused()
    {
        // Deliberately NOT `using`, and released in a finally.
        //
        // The worker parks inside release.Wait() for as long as this fact holds it there. Disposing
        // the event while it is parked throws ObjectDisposedException on a dedicated thread that has
        // no catch, and .NET terminates the PROCESS on an unhandled exception from such a thread.
        // Under `using`, a failing assertion below skips Set() and disposes anyway -- so the single
        // scenario this fact exists to report would take the whole test run down with it instead of
        // printing one red line. A fact whose failure destroys the evidence is worse than no fact.
        //
        // Never disposing costs one event handle for the life of the test process and cannot crash
        // anything. That is the cheaper side of this trade by a wide margin.
        var release = new ManualResetEventSlim(false);
        try
        {
            var outcome = Win32NativeWindowSource.RunBounded(
                () =>
                {
                    release.Wait();
                    return ActivationOutcome.Direct;
                },
                TimeSpan.FromMilliseconds(50));

            // Never Failed. Failed asserts the OS refused every rung, and nothing here asked the OS
            // anything -- reporting it would be the code claiming knowledge it does not have.
            Assert.Equal(ActivationOutcome.TimedOut, outcome);
        }
        finally
        {
            // Both paths, so a red assertion never leaves the worker parked for the rest of the run.
            release.Set();
        }
    }

    [Fact]
    public void RunBounded_WhenTheWorkerFinishesInTime_ReportsTheRungItReached()
    {
        var outcome = Win32NativeWindowSource.RunBounded(
            () => ActivationOutcome.InputUnlocked,
            LongEnoughForAnImmediateReturn);

        Assert.Equal(ActivationOutcome.InputUnlocked, outcome);
    }

    [Fact]
    public void RunBounded_WhenTheWorkerRefuses_KeepsSayingFailed()
    {
        var outcome = Win32NativeWindowSource.RunBounded(
            () => ActivationOutcome.Failed,
            LongEnoughForAnImmediateReturn);

        // The genuine refusal must survive the new member intact, or splitting the two endings would
        // just have moved the ambiguity rather than removed it.
        Assert.Equal(ActivationOutcome.Failed, outcome);
    }

    [Fact]
    public void TheBooleanContract_TreatsBothEndingsAsNotActivated()
    {
        // TryActivateWindow's contract is "the OS confirmed the target holds the foreground".
        // A timeout confirms nothing, so it must read false exactly like a refusal -- the split is
        // for the diagnosis, never for the caller.
        Assert.False(Win32NativeWindowSource.Activated(ActivationOutcome.Failed));
        Assert.False(Win32NativeWindowSource.Activated(ActivationOutcome.TimedOut));
    }

    [Fact]
    public void TheBooleanContract_StillAcceptsEveryVerifiedRung()
    {
        // Asserted one by one rather than through a loop over Enum.GetValues: a member added later
        // should force a decision here, not be silently swept into whichever side the loop assumed.
        Assert.True(Win32NativeWindowSource.Activated(ActivationOutcome.AlreadyForeground));
        Assert.True(Win32NativeWindowSource.Activated(ActivationOutcome.Direct));
        Assert.True(Win32NativeWindowSource.Activated(ActivationOutcome.AttachedInput));
        Assert.True(Win32NativeWindowSource.Activated(ActivationOutcome.InputUnlocked));
    }
}
