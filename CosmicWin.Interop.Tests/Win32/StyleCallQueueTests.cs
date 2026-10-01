using CosmicWin.Interop.Win32;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// A style call that timed out is ABANDONED, not cancelled: it can still land later. These facts pin
/// the rule that makes a later call on the same window safe -- it starts only after the abandoned
/// one has finished -- which is what lets a give-back land AFTER a strip that was still pending.
/// All headless: a call that "hangs" is a delegate parked on an event.
/// </summary>
public sealed class StyleCallQueueTests
{
    private static readonly TimeSpan Plenty = TimeSpan.FromSeconds(5);

    [Fact]
    public void ACallThatNeverAnswers_ReportsTimedOut_NotRefused()
    {
        var release = new ManualResetEventSlim(false);
        try
        {
            var outcome = new StyleCallQueue().Run(1, () => { release.Wait(); return true; }, TimeSpan.FromMilliseconds(50));

            Assert.Equal(StyleWriteOutcome.TimedOut, outcome);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public void ACompletedCall_ReportsAppliedOrRefused_AndAThrowingOneIsRefused()
    {
        var queue = new StyleCallQueue();

        Assert.Equal(StyleWriteOutcome.Applied, queue.Run(1, () => true, Plenty));
        Assert.Equal(StyleWriteOutcome.Refused, queue.Run(1, () => false, Plenty));
        Assert.Equal(StyleWriteOutcome.Refused, queue.Run(1, () => throw new InvalidOperationException("boom"), Plenty));
    }

    [Fact]
    public void ALaterCallOnTheSameWindow_RunsOnlyAfterTheAbandonedOneFinished()
    {
        // The strip hangs, times out and is abandoned. The give-back for the same window must not
        // run (and so must not read "box present" and skip itself) until the strip has landed.
        var queue = new StyleCallQueue();
        var release = new ManualResetEventSlim(false);
        var events = new List<string>();
        var gate = new object();
        void Record(string name)
        {
            lock (gate)
            {
                events.Add(name);
            }
        }

        try
        {
            var strip = queue.Run(7, () => { release.Wait(); Record("strip-landed"); return true; }, TimeSpan.FromMilliseconds(50));
            var giveBack = queue.Run(7, () => { Record("give-back"); return true; }, TimeSpan.FromMilliseconds(50));

            Assert.Equal(StyleWriteOutcome.TimedOut, strip);
            Assert.Equal(StyleWriteOutcome.TimedOut, giveBack); // still queued behind the strip
            lock (gate)
            {
                Assert.Empty(events); // the give-back did NOT run ahead of the strip
            }

            release.Set();

            Assert.True(SpinWait.SpinUntil(() => { lock (gate) { return events.Count == 2; } }, Plenty));
            lock (gate)
            {
                Assert.Equal(["strip-landed", "give-back"], events);
            }
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public void AHungWindow_DoesNotHoldUpACallOnAnotherWindow()
    {
        var queue = new StyleCallQueue();
        var release = new ManualResetEventSlim(false);
        try
        {
            _ = queue.Run(1, () => { release.Wait(); return true; }, TimeSpan.FromMilliseconds(50));

            Assert.Equal(StyleWriteOutcome.Applied, queue.Run(2, () => true, Plenty));
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public void AfterTheAbandonedCallFinishes_ANewCallRunsImmediately()
    {
        var queue = new StyleCallQueue();
        var release = new ManualResetEventSlim(false);
        var landed = new ManualResetEventSlim(false);
        _ = queue.Run(1, () => { release.Wait(); landed.Set(); return true; }, TimeSpan.FromMilliseconds(50));
        release.Set();
        Assert.True(landed.Wait(Plenty));

        Assert.Equal(StyleWriteOutcome.Applied, queue.Run(1, () => true, Plenty));
    }
}
