using CosmicWin.App.Alerts;

namespace CosmicWin.App.Tests.Alerts;

/// <summary>
/// A completion is superseded when a NEWER navigation has started since it: WebView2 aborts the older
/// one and reports it as a failure (ConnectionAborted), which must not tear the layer down. Any
/// completion of a non-latest navigation is ignored, success included: a stale success must not mark
/// the layer ready for the page that is still loading.
/// </summary>
public sealed class AlertLayerNavigationTests
{
    [Fact]
    public void CompletionOfTheLatestStartedNavigation_IsNotSuperseded() =>
        Assert.False(AlertLayerNavigation.IsSuperseded(completedId: 7, latestStartedId: 7));

    [Fact]
    public void CompletionOfAnOlderNavigation_IsSuperseded() =>
        Assert.True(AlertLayerNavigation.IsSuperseded(completedId: 6, latestStartedId: 7));

    [Fact]
    public void CompletionWhenNoNavigationStartWasRecorded_IsNotSuperseded() =>
        Assert.False(AlertLayerNavigation.IsSuperseded(completedId: 3, latestStartedId: null));

    [Fact]
    public void CompletionIdAheadOfTheRecordedOne_IsNotSuperseded() =>
        Assert.False(AlertLayerNavigation.IsSuperseded(completedId: 9, latestStartedId: 7));
}
