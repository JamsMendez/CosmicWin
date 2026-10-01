namespace CosmicWin.App.Alerts;

/// <summary>
/// The pure decision behind "is this NavigationCompleted stale?". A scene switch calls Navigate while
/// the previous navigation may still be in flight; WebView2 then aborts the older one and completes it
/// with IsSuccess=false (ConnectionAborted). That completion must not tear the layer down, and a stale
/// success must not mark the layer ready for the page that is still loading, so EVERY completion that
/// is not for the latest started navigation is ignored. With no start recorded (should not happen) the
/// completion is handled as before.
/// </summary>
internal static class AlertLayerNavigation
{
    public static bool IsSuperseded(ulong completedId, ulong? latestStartedId) =>
        latestStartedId is { } latest && completedId < latest;
}
