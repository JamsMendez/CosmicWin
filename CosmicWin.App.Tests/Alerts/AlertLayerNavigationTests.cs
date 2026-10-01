using CosmicWin.App.Alerts;

namespace CosmicWin.App.Tests.Alerts;

public sealed class AlertLayerNavigationTests
{
    [Fact]
    public void CompletionOfTheCurrentNavigation_IsNotSuperseded()
    {
        var nav = new AlertLayerNavigation();
        nav.Started(7);
        Assert.False(nav.CompletedIsSuperseded(7));
    }

    /// <summary>The real order seen on hardware: the abort completion arrives BEFORE the new NavigationStarting.</summary>
    [Fact]
    public void AbortCompletionBeforeTheNewStarting_IsSuperseded_AndTheNewOneStillCounts()
    {
        var nav = new AlertLayerNavigation();
        nav.Started(5);
        nav.BeforeHostNavigate();
        Assert.True(nav.CompletedIsSuperseded(5));
        nav.Started(6);
        Assert.False(nav.CompletedIsSuperseded(6));
    }

    [Fact]
    public void AbortCompletionAfterTheNewStarting_IsSuperseded()
    {
        var nav = new AlertLayerNavigation();
        nav.Started(5);
        nav.BeforeHostNavigate();
        nav.Started(6);
        Assert.True(nav.CompletedIsSuperseded(5));
        Assert.False(nav.CompletedIsSuperseded(6));
    }

    /// <summary>Ids are matched exactly, never ordered: a non-monotonic id is not treated as stale.</summary>
    [Fact]
    public void NonMonotonicIds_AreMatchedExactly()
    {
        var nav = new AlertLayerNavigation();
        nav.Started(9);
        nav.BeforeHostNavigate();
        nav.Started(3);
        Assert.False(nav.CompletedIsSuperseded(3));
        Assert.True(nav.CompletedIsSuperseded(9));
    }

    [Fact]
    public void ABurstOfNavigations_AbandonsEachInFlightOne()
    {
        var nav = new AlertLayerNavigation();
        nav.Started(1);
        nav.BeforeHostNavigate();
        nav.Started(2);
        nav.BeforeHostNavigate();
        nav.Started(3);
        Assert.True(nav.CompletedIsSuperseded(1));
        Assert.True(nav.CompletedIsSuperseded(2));
        Assert.False(nav.CompletedIsSuperseded(3));
    }

    [Fact]
    public void HostNavigateAfterTheNavigationCompleted_AbandonsNothing()
    {
        var nav = new AlertLayerNavigation();
        nav.Started(1);
        Assert.False(nav.CompletedIsSuperseded(1));
        nav.BeforeHostNavigate();
        Assert.False(nav.CompletedIsSuperseded(1));
    }

    [Fact]
    public void ASupersededIdIsConsumed_SoALaterReuseOfItIsNotSuperseded()
    {
        var nav = new AlertLayerNavigation();
        nav.Started(1);
        nav.BeforeHostNavigate();
        Assert.True(nav.CompletedIsSuperseded(1));
        Assert.False(nav.CompletedIsSuperseded(1));
    }

    [Fact]
    public void Clear_ForgetsAbandonedAndInFlight()
    {
        var nav = new AlertLayerNavigation();
        nav.Started(1);
        nav.BeforeHostNavigate();
        nav.Clear();
        Assert.False(nav.CompletedIsSuperseded(1));
    }

    /// <summary>
    /// Review R3-navigate-before-starting-race: the host navigates twice before NavigationStarting for the
    /// first is delivered, so BeforeHostNavigate had nothing in flight to abandon. Both starts then arrive
    /// and the first one's abort completes last: a navigation that started after it supersedes it.
    /// </summary>
    [Fact]
    public void TwoHostNavigatesBeforeEitherStarted_TheOlderAbortIsSuperseded_AndTheNewerStillCounts()
    {
        var nav = new AlertLayerNavigation();
        nav.BeforeHostNavigate();
        nav.BeforeHostNavigate();
        nav.Started(11);
        nav.Started(12);
        Assert.True(nav.CompletedIsSuperseded(11));
        Assert.False(nav.CompletedIsSuperseded(12));
    }

    /// <summary>Review R3-reset-latest-started-untested: a teardown must forget the previous controller's latest start.</summary>
    [Fact]
    public void Clear_ForgetsTheLatestStart_SoTheNextControllersFirstCompletionCounts()
    {
        var nav = new AlertLayerNavigation();
        nav.Started(1);
        nav.Clear();
        Assert.False(nav.CompletedIsSuperseded(2));
    }

    [Fact]
    public void CompletionWithNothingRecorded_IsNotSuperseded() =>
        Assert.False(new AlertLayerNavigation().CompletedIsSuperseded(3));
}
