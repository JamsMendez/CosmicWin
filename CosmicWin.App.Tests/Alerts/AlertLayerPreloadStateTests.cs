using CosmicWin.App.Alerts;

namespace CosmicWin.App.Tests.Alerts;

/// <summary>
/// T9c (webview-alert-layer): <see cref="AlertLayerPreloadState"/> is the PURE state machine behind
/// the persistent preloaded alert layer -- host identity/backoff tracking, readiness, and the one
/// pending show a caller may register before the page is ready. Unit-tested directly here so the
/// hardware-only real-WebView2 concern (<see cref="WebViewAlertLayerController"/>'s own creation
/// path) never has to be exercised to prove this logic -- same split T3/T6 already established.
/// </summary>
public sealed class AlertLayerPreloadStateTests
{
    [Fact]
    public void HostChangedReportsTrueOnFirstObservationAndOnAGenerationChangeOnly()
    {
        var state = new AlertLayerPreloadState();
        Assert.True(state.HostChanged(10, 1), "first observation");
        Assert.False(state.HostChanged(10, 1), "unchanged identity");
        Assert.False(state.HostChanged(10, 1), "still unchanged");
        Assert.True(state.HostChanged(10, 2), "generation bumped (Explorer restart)");
        Assert.True(state.HostChanged(20, 2), "hwnd changed");
        Assert.False(state.HostChanged(20, 2), "settled again");
    }

    [Fact]
    public void FailedBacksOffExponentiallyAndCanCreateFlipsAfterRetryAfter()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new AlertLayerPreloadState(() => now);
        Assert.True(state.CanCreate);

        state.Failed();
        Assert.False(state.CanCreate);
        now = now.AddMilliseconds(499);
        Assert.False(state.CanCreate);
        now = now.AddMilliseconds(2);
        Assert.True(state.CanCreate, "first backoff is 500ms");

        state.Failed();
        now = now.AddMilliseconds(999);
        Assert.False(state.CanCreate);
        now = now.AddMilliseconds(2);
        Assert.True(state.CanCreate, "second backoff doubles to 1000ms");

        state.Created();
        state.Failed();
        now = now.AddMilliseconds(499);
        Assert.False(state.CanCreate, "a successful Created() resets the backoff to the first step again");
    }

    [Fact]
    public void FailedAlsoClearsReadyAndVisible()
    {
        var state = new AlertLayerPreloadState();
        state.MarkReady();
        state.RequestShow("warning", 1000);
        Assert.True(state.Ready);
        Assert.True(state.Visible);

        state.Failed();
        Assert.False(state.Ready, "a failed/lost controller cannot still be ready");
        Assert.False(state.Visible, "nor can it still be showing anything");
    }

    [Fact]
    public void RequestShowPostsImmediatelyAndMarksVisibleWhileReadyEvenWhenAlreadyVisible()
    {
        var state = new AlertLayerPreloadState();
        state.MarkReady();

        var first = state.RequestShow("warning", 1000);
        Assert.Equal(("warning", 1000), first);
        Assert.True(state.Visible);

        // Start while visible re-shows: a second Start must still post, not be swallowed as a no-op.
        var second = state.RequestShow("failed", 2000);
        Assert.Equal(("failed", 2000), second);
        Assert.True(state.Visible);
    }

    [Fact]
    public void RequestShowWhileNotReadyStoresAPendingShowAndPostsNothingYet()
    {
        var state = new AlertLayerPreloadState();
        var posted = state.RequestShow("warning", 1000);
        Assert.Null(posted);
        Assert.False(state.Visible);
    }

    [Fact]
    public void PendingShowIsAppliedWithTheRemainingDurationOnceReady()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new AlertLayerPreloadState(() => now);
        state.RequestShow("warning", 1000);
        now = now.AddMilliseconds(400);

        state.MarkReady();
        var applied = state.ApplyPendingShowIfDue();

        Assert.NotNull(applied);
        Assert.Equal("warning", applied.Value.Kind);
        // ~600ms left of the original 1000ms -- ceiling-rounded, never the ORIGINAL duration again.
        Assert.InRange(applied.Value.DurationMilliseconds, 599, 601);
        Assert.True(state.Visible);
    }

    [Fact]
    public void PendingShowIsDroppedWhenItsDeadlineAlreadyPassedBeforeBecomingReady()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new AlertLayerPreloadState(() => now);
        state.RequestShow("warning", 1000);
        now = now.AddMilliseconds(1001);

        state.MarkReady();
        var applied = state.ApplyPendingShowIfDue();

        Assert.Null(applied);
        Assert.False(state.Visible, "an expired pending show must never make the layer visible");
    }

    [Fact]
    public void ApplyPendingShowIfDueIsANoOpWhenNothingWasPending()
    {
        var state = new AlertLayerPreloadState();
        state.MarkReady();
        Assert.Null(state.ApplyPendingShowIfDue());
    }

    [Fact]
    public void HideClearsVisibilityAndAnyPendingShowWithoutTouchingReadiness()
    {
        var state = new AlertLayerPreloadState();
        state.MarkReady();
        state.RequestShow("warning", 1000);
        Assert.True(state.Visible);

        state.Hide();

        Assert.False(state.Visible);
        Assert.True(state.Ready, "hiding the page must not close/tear down the controller");
        // Hide also cancels any not-yet-applied pending show.
        Assert.Null(state.ApplyPendingShowIfDue());
    }

    [Fact]
    public void PageDoneHidesWithoutAffectingReadiness()
    {
        var state = new AlertLayerPreloadState();
        state.MarkReady();
        state.RequestShow("warning", 1000);

        state.PageDone();

        Assert.False(state.Visible);
        Assert.True(state.Ready);
    }

    [Fact]
    public void ControllerLostClearsReadySoALaterShowIsKeptPendingAndAppliedOnceReadyAgain()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new AlertLayerPreloadState(() => now);
        state.MarkReady();

        state.ControllerLost();
        Assert.False(state.Ready, "the replacement controller has not navigated/reported ready yet");

        var posted = state.RequestShow("warning", 1000);
        Assert.Null(posted);
        now = now.AddMilliseconds(400);

        state.MarkReady();
        var applied = state.ApplyPendingShowIfDue();

        Assert.NotNull(applied);
        Assert.Equal("warning", applied.Value.Kind);
        Assert.InRange(applied.Value.DurationMilliseconds, 599, 601);
        Assert.True(state.Visible);
    }

    [Fact]
    public void ControllerLostWhileShowingRequeuesTheRemainingDurationOfTheShownAlert()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new AlertLayerPreloadState(() => now);
        state.MarkReady();
        state.RequestShow("failed", 5000);
        now = now.AddMilliseconds(2000);

        state.ControllerLost();
        Assert.False(state.Visible, "the old controller is gone -- nothing is on screen any more");
        Assert.False(state.Ready);
        Assert.True(state.CanCreate, "ControllerLost must never touch the Failed() backoff");

        now = now.AddMilliseconds(500);
        state.MarkReady();
        var applied = state.ApplyPendingShowIfDue();

        Assert.NotNull(applied);
        Assert.Equal(("failed", 2500), applied);
        Assert.True(state.Visible);
    }
}
