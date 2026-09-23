using CosmicWin.App.Alerts;

namespace CosmicWin.App.Tests.Alerts;

/// <summary>
/// Which parsed alert -- if any -- is on screen at a given instant, decided 2026-09-23 (plan
/// &#167;4/&#167;6, T2; queued-while-covered behaviour decided by the maintainer the same day, see
/// "Decided: alerts while the desktop is covered" in the feature's task file).
/// </summary>
/// <remarks>
/// Pure and time-free, exactly like <see cref="AlertQueue"/> itself: every test drives
/// <see cref="AlertQueue.Advance"/> with an explicit <see cref="DateTimeOffset"/> rather than a
/// real clock, and small capacities/max ages rather than the production defaults.
/// </remarks>
public sealed class AlertQueueTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 9, 23, 0, 0, 0, TimeSpan.Zero);

    private static AlertCommand Command(int durationSeconds = 5, AlertKind kind = AlertKind.Warning) =>
        new([new AlertGroup(kind, 1)], TimeSpan.FromSeconds(durationSeconds));

    [Fact]
    public void AnEmptyQueue_HasNothingToShow()
    {
        var queue = new AlertQueue();

        Assert.Null(queue.Advance(Epoch, desktopVisible: true));
    }

    [Fact]
    public void TheFirstEnqueuedAlert_IsTheFirstShown()
    {
        var queue = new AlertQueue();
        var first = Command();
        var second = Command();
        queue.Enqueue(first, Epoch);
        queue.Enqueue(second, Epoch);

        var active = queue.Advance(Epoch, desktopVisible: true);

        Assert.NotNull(active);
        Assert.Same(first, active!.Command);
        Assert.Equal(Epoch, active.StartedAt);
    }

    [Fact]
    public void UpToCapacity_EveryEnqueueSucceeds_AndOneMoreIsRejected()
    {
        var queue = new AlertQueue(capacity: 2);

        Assert.True(queue.Enqueue(Command(), Epoch));
        Assert.True(queue.Enqueue(Command(), Epoch));
        Assert.False(queue.Enqueue(Command(), Epoch));
    }

    [Fact]
    public void ARejectionFromAFullQueue_IsReported()
    {
        var messages = new List<string>();
        var queue = new AlertQueue(capacity: 1, onDiagnostic: messages.Add);
        queue.Enqueue(Command(), Epoch);

        var accepted = queue.Enqueue(Command(), Epoch);

        Assert.False(accepted);
        Assert.Single(messages);
    }

    /// <summary>A full queue rejects the NEW command; it must never evict an older queued one.</summary>
    [Fact]
    public void ARejectedAlert_NeverDisplacesAnOlderQueuedOne()
    {
        var queue = new AlertQueue(capacity: 1);
        var kept = Command();
        queue.Enqueue(kept, Epoch);
        queue.Enqueue(Command(), Epoch);

        var active = queue.Advance(Epoch, desktopVisible: true);

        Assert.Same(kept, active!.Command);
    }

    [Fact]
    public void NothingStarts_WhileTheDesktopIsCovered()
    {
        var queue = new AlertQueue();
        queue.Enqueue(Command(), Epoch);

        Assert.Null(queue.Advance(Epoch, desktopVisible: false));
    }

    [Fact]
    public void AQueuedAlert_StartsAsSoonAsTheDesktopBecomesVisible()
    {
        var queue = new AlertQueue();
        var command = Command();
        queue.Enqueue(command, Epoch);
        queue.Advance(Epoch, desktopVisible: false);

        var startedAt = Epoch.AddSeconds(30);
        var active = queue.Advance(startedAt, desktopVisible: true);

        Assert.NotNull(active);
        Assert.Same(command, active!.Command);
        Assert.Equal(startedAt, active.StartedAt);
    }

    [Fact]
    public void TheDesktopBecomingCovered_MidDisplay_DoesNotExtendIt()
    {
        var queue = new AlertQueue();
        queue.Enqueue(Command(durationSeconds: 5), Epoch);
        queue.Advance(Epoch, desktopVisible: true);

        // Covered partway through: the clock keeps running regardless of visibility.
        var stillActive = queue.Advance(Epoch.AddSeconds(3), desktopVisible: false);
        Assert.NotNull(stillActive);

        // Duration elapses while still covered: it ends on time, never paused or extended.
        var afterDuration = queue.Advance(Epoch.AddSeconds(5), desktopVisible: false);
        Assert.Null(afterDuration);
    }

    [Fact]
    public void AnAlert_EndsExactlyAtStartPlusDuration()
    {
        var queue = new AlertQueue();
        queue.Enqueue(Command(durationSeconds: 5), Epoch);
        queue.Advance(Epoch, desktopVisible: true);

        var stillShowing = queue.Advance(Epoch.AddSeconds(5).AddTicks(-1), desktopVisible: true);
        var ended = queue.Advance(Epoch.AddSeconds(5), desktopVisible: true);

        Assert.NotNull(stillShowing);
        Assert.Null(ended);
    }

    [Fact]
    public void WhenOneAlertEnds_TheNextEligibleOneStartsOnTheSameAdvanceCall()
    {
        var queue = new AlertQueue();
        var first = Command(durationSeconds: 5);
        var second = Command(durationSeconds: 5);
        queue.Enqueue(first, Epoch);
        queue.Enqueue(second, Epoch);
        queue.Advance(Epoch, desktopVisible: true);

        var endTime = Epoch.AddSeconds(5);
        var active = queue.Advance(endTime, desktopVisible: true);

        Assert.NotNull(active);
        Assert.Same(second, active!.Command);
        Assert.Equal(endTime, active.StartedAt);
    }

    /// <summary>Boundary, decided here: exactly the max age is still eligible; past it is dropped.</summary>
    [Fact]
    public void AnAlertAtExactlyTheMaxAge_IsStillEligible()
    {
        var maxAge = TimeSpan.FromMinutes(1);
        var queue = new AlertQueue(maxAge: maxAge);
        var command = Command();
        queue.Enqueue(command, Epoch);

        var active = queue.Advance(Epoch + maxAge, desktopVisible: true);

        Assert.NotNull(active);
        Assert.Same(command, active!.Command);
    }

    [Fact]
    public void AnAlertPastTheMaxAge_IsDroppedAndReported()
    {
        var maxAge = TimeSpan.FromMinutes(1);
        var messages = new List<string>();
        var queue = new AlertQueue(maxAge: maxAge, onDiagnostic: messages.Add);
        queue.Enqueue(Command(), Epoch);

        var active = queue.Advance(Epoch + maxAge + TimeSpan.FromTicks(1), desktopVisible: true);

        Assert.Null(active);
        Assert.Single(messages);
    }

    /// <summary>The drop is noticed on the next <see cref="AlertQueue.Advance"/> call regardless of
    /// visibility -- an alert past its max age is never worth starting, covered or not.</summary>
    [Fact]
    public void AnAlertPastTheMaxAge_IsDropped_EvenWhileTheDesktopStaysCovered()
    {
        var maxAge = TimeSpan.FromMinutes(1);
        var messages = new List<string>();
        var queue = new AlertQueue(maxAge: maxAge, onDiagnostic: messages.Add);
        queue.Enqueue(Command(), Epoch);

        var active = queue.Advance(Epoch + maxAge + TimeSpan.FromSeconds(1), desktopVisible: false);

        Assert.Null(active);
        Assert.Single(messages);
    }
}
