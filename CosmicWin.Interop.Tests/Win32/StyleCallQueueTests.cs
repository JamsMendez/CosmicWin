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

    [Fact]
    public void ManyCallsOnAParkedWindow_ParkOneWorker_AndOnlyTheLatestRequestLandsAfterTheFirst()
    {
        // 50 style writes pile up on a window that never answers. The bound is real: one worker is
        // parked and one request is pending, whatever the number of callers; the newest request
        // replaces the one before it, so once the window wakes the executed writes are the one that
        // was already running and the LAST one issued -- nothing in between.
        var queue = new StyleCallQueue();
        var release = new ManualResetEventSlim(false);
        var started = new ManualResetEventSlim(false);
        var executed = new List<int>();
        var threads = new HashSet<int>();
        var gate = new object();
        var done = new ManualResetEventSlim(false);
        bool Call(int wanted)
        {
            lock (gate)
            {
                executed.Add(wanted);
                threads.Add(Environment.CurrentManagedThreadId);
            }

            return true;
        }

        try
        {
            _ = queue.Run(9, () => { started.Set(); release.Wait(); Call(0); return true; }, TimeSpan.FromMilliseconds(50));
            Assert.True(started.Wait(Plenty));
            for (var i = 1; i <= 50; i++)
            {
                var wanted = i;
                var outcome = queue.Run(9, () => { Call(wanted); if (wanted == 50) { done.Set(); } return true; }, TimeSpan.FromMilliseconds(1));
                Assert.Equal(StyleWriteOutcome.TimedOut, outcome);
            }

            Assert.Equal(1, queue.WorkerCount);
            Assert.True(queue.HasPending(9));

            release.Set();
            Assert.True(done.Wait(Plenty));

            lock (gate)
            {
                Assert.Equal([0, 50], executed);
                Assert.Single(threads);
            }
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public void ASupersededPendingCall_StopsWaitingAtOnce_AndReportsTimedOut()
    {
        var queue = new StyleCallQueue();
        var release = new ManualResetEventSlim(false);
        var started = new ManualResetEventSlim(false);
        var ran = new List<string>();
        var gate = new object();
        var landed = new ManualResetEventSlim(false);
        try
        {
            _ = queue.Run(3, () => { started.Set(); release.Wait(); return true; }, TimeSpan.FromMilliseconds(50));
            Assert.True(started.Wait(Plenty));

            var older = Task.Run(() => queue.Run(3, () => { lock (gate) { ran.Add("older"); } return true; }, TimeSpan.FromSeconds(30)));
            Assert.True(SpinWait.SpinUntil(() => queue.HasPending(3), Plenty));

            _ = queue.Run(3, () => { lock (gate) { ran.Add("newer"); } landed.Set(); return true; }, TimeSpan.FromMilliseconds(1));

            Assert.True(older.Wait(Plenty)); // released by the replacement, not by its 30 s budget
            Assert.Equal(StyleWriteOutcome.TimedOut, older.Result);

            release.Set();
            Assert.True(landed.Wait(Plenty));
            lock (gate)
            {
                Assert.Equal(["newer"], ran);
            }
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public void AnIndependentCall_IsNotQueuedBehindAParkedOrderedCall_OnTheSameWindow()
    {
        var queue = new StyleCallQueue();
        var release = new ManualResetEventSlim(false);
        try
        {
            _ = queue.Run(5, () => { release.Wait(); return true; }, TimeSpan.FromMilliseconds(50));

            Assert.Equal(StyleWriteOutcome.Applied, queue.RunIndependent(5, () => true, Plenty));
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public void RepeatedIndependentCalls_OnAWindowThatNeverAnswers_ParkOneWorker()
    {
        var queue = new StyleCallQueue();
        var release = new ManualResetEventSlim(false);
        var started = new ManualResetEventSlim(false);
        var starts = 0;
        try
        {
            var first = queue.RunIndependent(6, () => { Interlocked.Increment(ref starts); started.Set(); release.Wait(); return true; }, TimeSpan.FromMilliseconds(50));
            Assert.Equal(StyleWriteOutcome.TimedOut, first);
            Assert.True(started.Wait(Plenty));

            for (var i = 0; i < 20; i++)
            {
                Assert.Equal(StyleWriteOutcome.TimedOut, queue.RunIndependent(6, () => { Interlocked.Increment(ref starts); return true; }, TimeSpan.FromMilliseconds(1)));
            }

            Assert.Equal(1, queue.WorkerCount);
            release.Set();
            Assert.True(SpinWait.SpinUntil(() => queue.WorkerCount == 0, Plenty));
            Assert.Equal(1, Volatile.Read(ref starts)); // the refused ones never even started
        }
        finally
        {
            release.Set();
        }
    }
}
