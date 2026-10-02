using CosmicWin.App.Alerts;

namespace CosmicWin.App.Tests.Alerts;

/// <summary>
/// T9c (webview-alert-layer): <see cref="AlertLayerPreloadState"/> is the PURE state machine behind
/// the persistent preloaded alert layer -- host identity/backoff tracking, readiness, and the one
/// pending show a caller may register before the page is ready. Unit-tested directly here so the
/// hardware-only real-WebView2 concern (<see cref="WebViewAlertLayerController"/>'s own creation
/// path) never has to be exercised to prove this logic -- same split T3/T6 already established.
/// </summary>
/// <remarks>
/// alert-tile-mosaic (2026-09-26): rewritten from the single-kind contract (<c>RequestShow(string
/// kind, int durationMilliseconds)</c>) to carry a whole <see cref="AlertShowRequest"/> (tile list,
/// grid, gap, duration) through unchanged -- the state machine's job stays exactly the same
/// (readiness/pending/backoff), it just carries a bigger payload now.
/// </remarks>
public sealed class AlertLayerPreloadStateTests
{
    private static AlertShowRequest Show(string kind, int durationMilliseconds) =>
        new([kind], 1, 1, 8, durationMilliseconds);

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
        state.RequestShow(Show("warning", 1000));
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

        var firstRequest = Show("warning", 1000);
        var first = state.RequestShow(firstRequest);
        Assert.Equal(firstRequest, first);
        Assert.True(state.Visible);

        // Start while visible re-shows: a second Start must still post, not be swallowed as a no-op.
        var secondRequest = Show("failed", 2000);
        var second = state.RequestShow(secondRequest);
        Assert.Equal(secondRequest, second);
        Assert.True(state.Visible);
    }

    [Fact]
    public void RequestShowWhileNotReadyStoresAPendingShowAndPostsNothingYet()
    {
        var state = new AlertLayerPreloadState();
        var posted = state.RequestShow(Show("warning", 1000));
        Assert.Null(posted);
        Assert.False(state.Visible);
    }

    [Fact]
    public void PendingShowIsAppliedWithTheRemainingDurationOnceReady()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new AlertLayerPreloadState(() => now);
        state.RequestShow(Show("warning", 1000));
        now = now.AddMilliseconds(400);

        state.MarkReady();
        var applied = state.ApplyPendingShowIfDue();

        Assert.NotNull(applied);
        Assert.Equal(new[] { "warning" }, applied!.Tiles);
        // ~600ms left of the original 1000ms -- ceiling-rounded, never the ORIGINAL duration again.
        Assert.InRange(applied.DurationMilliseconds, 599, 601);
        Assert.True(state.Visible);
    }

    [Fact]
    public void PendingShowCarriesTheGridAndGapUnchanged()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new AlertLayerPreloadState(() => now);
        state.RequestShow(new AlertShowRequest(["failed", "failed", "warning"], 2, 2, 12, 1000));
        now = now.AddMilliseconds(400);

        state.MarkReady();
        var applied = state.ApplyPendingShowIfDue();

        Assert.NotNull(applied);
        Assert.Equal(new[] { "failed", "failed", "warning" }, applied!.Tiles);
        Assert.Equal(2, applied.Columns);
        Assert.Equal(2, applied.Rows);
        Assert.Equal(12, applied.Gap);
    }

    /// <summary>T7 (alert-tile-mosaic): the work area rides through the pending/shown tracking unchanged, same as the grid and gap above.</summary>
    [Fact]
    public void PendingShowCarriesTheWorkAreaUnchanged()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new AlertLayerPreloadState(() => now);
        state.RequestShow(new AlertShowRequest(
            ["warning"], 1, 1, 8, 1000,
            WorkAreaLeft: 10, WorkAreaTop: 20, WorkAreaWidth: 1700, WorkAreaHeight: 1000));
        now = now.AddMilliseconds(400);

        state.MarkReady();
        var applied = state.ApplyPendingShowIfDue();

        Assert.NotNull(applied);
        Assert.Equal(10, applied.WorkAreaLeft);
        Assert.Equal(20, applied.WorkAreaTop);
        Assert.Equal(1700, applied.WorkAreaWidth);
        Assert.Equal(1000, applied.WorkAreaHeight);
    }

    [Fact]
    public void PendingShowIsDroppedWhenItsDeadlineAlreadyPassedBeforeBecomingReady()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new AlertLayerPreloadState(() => now);
        state.RequestShow(Show("warning", 1000));
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
        state.RequestShow(Show("warning", 1000));
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
        state.RequestShow(Show("warning", 1000));

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

        var posted = state.RequestShow(Show("warning", 1000));
        Assert.Null(posted);
        now = now.AddMilliseconds(400);

        state.MarkReady();
        var applied = state.ApplyPendingShowIfDue();

        Assert.NotNull(applied);
        Assert.Equal(new[] { "warning" }, applied!.Tiles);
        Assert.InRange(applied.DurationMilliseconds, 599, 601);
        Assert.True(state.Visible);
    }

    [Fact]
    public void ControllerLostWhileShowingRequeuesTheRemainingDurationOfTheShownAlert()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new AlertLayerPreloadState(() => now);
        state.MarkReady();
        state.RequestShow(Show("failed", 5000));
        now = now.AddMilliseconds(2000);

        state.ControllerLost();
        Assert.False(state.Visible, "the old controller is gone -- nothing is on screen any more");
        Assert.False(state.Ready);
        Assert.True(state.CanCreate, "ControllerLost must never touch the Failed() backoff");

        now = now.AddMilliseconds(500);
        state.MarkReady();
        var applied = state.ApplyPendingShowIfDue();

        Assert.NotNull(applied);
        Assert.Equal(new[] { "failed" }, applied!.Tiles);
        Assert.Equal(2500, applied.DurationMilliseconds);
        Assert.True(state.Visible);
    }
}
