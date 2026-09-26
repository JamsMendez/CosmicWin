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
        // Busy-ignore (alert-busy-ignore): this second call is ignored, not queued behind the
        // first -- it exists only to prove the ignored one never displaces the queued one either.
        var ignored = Command();
        queue.Enqueue(first, Epoch);
        queue.Enqueue(ignored, Epoch);

        var active = queue.Advance(Epoch, desktopVisible: true);

        Assert.NotNull(active);
        Assert.Same(first, active!.Command);
        Assert.Equal(Epoch, active.StartedAt);
    }

    /// <summary>
    /// Rewritten for alert-busy-ignore (maintainer decision, 2026-09-26): these three tests used to
    /// prove capacity rejection by enqueuing past a small <c>capacity</c> while nothing was ever
    /// consumed. That path is no longer reachable through <see cref="AlertQueue.Enqueue"/>: with at
    /// most one alert ever showing or waiting, a second call while one is already pending is now
    /// caught by the busy-ignore rule (see <see cref="ASecondRequest_IsIgnoredWhileOneIsWaiting"/>
    /// and friends below) before the capacity check is ever reached -- with any capacity of 1 or
    /// more, <c>_pending.Count</c> can never grow past 1 through <c>Enqueue</c> alone, so
    /// <c>_pending.Count &gt;= _capacity</c> can only be true when <c>capacity</c> is itself 1, which
    /// the busy-ignore check already intercepts first. The capacity field, its constructor
    /// validation, and the internal capacity-full branch are kept as a defensive invariant guard
    /// (see the constructor tests below, still exercised), not removed, per the feature's decision
    /// that queue-full stays in the code even though the composition can no longer reach it.
    /// </summary>
    [Fact]
    public void ASecondRequest_IsIgnoredWhileOneIsShowing()
    {
        var messages = new List<string>();
        var queue = new AlertQueue(onDiagnostic: messages.Add);
        var showing = Command(durationSeconds: 5);
        queue.Enqueue(showing, Epoch);
        queue.Advance(Epoch, desktopVisible: true);

        var accepted = queue.Enqueue(Command(), Epoch.AddSeconds(2));

        Assert.True(accepted);
        Assert.Single(messages, m => m.StartsWith("alert ignored:", StringComparison.Ordinal) &&
            m.Contains("showing", StringComparison.Ordinal));
    }

    /// <summary>
    /// A request arriving exactly when (or after) the showing alert's window has elapsed is judged
    /// against <c>now</c>, not against state only <see cref="AlertQueue.Advance"/> clears -- it must
    /// be accepted and queued even though nobody has called <c>Advance</c> to end the old one yet,
    /// and it starts on that same next <c>Advance</c> call. Replaces the old
    /// <c>WhenOneAlertEnds_TheNextEligibleOneStartsOnTheSameAdvanceCall</c>, which enqueued both
    /// alerts back to back at the SAME instant -- exactly the stacking the new busy-ignore rule
    /// forbids, since the second would now be ignored rather than queued.
    /// </summary>
    [Fact]
    public void ARequestRightAfterTheWindowEnds_IsAcceptedBeforeAnyAdvanceRuns_AndStartsOnTheNextAdvance()
    {
        var queue = new AlertQueue();
        var first = Command(durationSeconds: 5);
        var second = Command(durationSeconds: 5);
        queue.Enqueue(first, Epoch);
        queue.Advance(Epoch, desktopVisible: true);

        var endTime = Epoch.AddSeconds(5);
        var accepted = queue.Enqueue(second, endTime);
        Assert.True(accepted);

        var active = queue.Advance(endTime, desktopVisible: true);

        Assert.NotNull(active);
        Assert.Same(second, active!.Command);
        Assert.Equal(endTime, active.StartedAt);
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

    /// <summary>
    /// alert-busy-ignore, maintainer decision 4: a waiting alert (queued while the desktop is
    /// covered, never yet shown) counts as "in progress" too -- a second request while it is still
    /// waiting and not expired is ignored, exactly like a second request while one is showing.
    /// </summary>
    [Fact]
    public void ASecondRequest_IsIgnoredWhileOneIsWaiting()
    {
        var messages = new List<string>();
        var queue = new AlertQueue(onDiagnostic: messages.Add);
        var waiting = Command();
        queue.Enqueue(waiting, Epoch);
        queue.Advance(Epoch, desktopVisible: false);

        var accepted = queue.Enqueue(Command(), Epoch.AddSeconds(1));

        Assert.True(accepted);
        Assert.Single(messages, m => m.StartsWith("alert ignored:", StringComparison.Ordinal) &&
            m.Contains("waiting", StringComparison.Ordinal));

        // Never shows later: the desktop becoming visible only ever starts the one alert that was
        // actually queued.
        var active = queue.Advance(Epoch.AddSeconds(2), desktopVisible: true);
        Assert.Same(waiting, active!.Command);
        Assert.Null(queue.Advance(active.StartedAt + waiting.Duration, desktopVisible: true));
    }

    /// <summary>
    /// A waiting alert past its max age must not swallow a new request either -- <see
    /// cref="AlertQueue.Enqueue"/> reuses <c>DropExpired</c> so the drop is still reported, and the
    /// new request lands in the now-empty queue instead of being ignored.
    /// </summary>
    [Fact]
    public void ARequest_IsAcceptedOnceThePreviouslyWaitingOneHasExpired()
    {
        var maxAge = TimeSpan.FromMinutes(1);
        var messages = new List<string>();
        var queue = new AlertQueue(maxAge: maxAge, onDiagnostic: messages.Add);
        var expired = Command();
        queue.Enqueue(expired, Epoch);
        queue.Advance(Epoch, desktopVisible: false);

        var replacement = Command();
        var now = Epoch + maxAge + TimeSpan.FromTicks(1);
        var accepted = queue.Enqueue(replacement, now);

        Assert.True(accepted);
        Assert.Single(messages, m => m.StartsWith("alert dropped:", StringComparison.Ordinal));

        var active = queue.Advance(now, desktopVisible: true);
        Assert.Same(replacement, active!.Command);
    }

    /// <summary>Finding R3-queue-ctor-unvalidated: a non-positive capacity can never hold a single
    /// alert, so it is a construction error rather than a queue that silently rejects everything.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_NonPositiveCapacity_Throws(int capacity) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new AlertQueue(capacity: capacity));

    /// <summary>Finding R3-queue-ctor-unvalidated: a negative max age can never be "waited longer
    /// than", which would make every enqueue immediately eligible for drop AND for a bogus negative
    /// duration comparison -- reject it at construction instead.</summary>
    [Fact]
    public void Constructor_NegativeMaxAge_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new AlertQueue(maxAge: TimeSpan.FromTicks(-1)));

    /// <summary>Decided here (finding R3-queue-ctor-unvalidated): a ZERO max age is allowed -- it
    /// means "this alert must start immediately or be dropped," not "reject the queue." An alert
    /// enqueued and advanced at the very same instant is still exactly at the max age (see
    /// <see cref="AnAlertAtExactlyTheMaxAge_IsStillEligible"/>'s boundary), so it starts on that
    /// same call.</summary>
    [Fact]
    public void Constructor_ZeroMaxAge_IsAllowed_AndTheAlertStartsOnTheSameInstantItWasEnqueued()
    {
        var queue = new AlertQueue(maxAge: TimeSpan.Zero);
        var command = Command();
        queue.Enqueue(command, Epoch);

        var active = queue.Advance(Epoch, desktopVisible: true);

        Assert.NotNull(active);
        Assert.Same(command, active!.Command);
    }
}
