namespace CosmicWin.App.Alerts;

/// <summary>
/// Pure bookkeeping behind "is this NavigationCompleted stale?", independent of event order and of id
/// ordering. When the HOST calls Navigate while a navigation is still in flight, WebView2 aborts the old
/// one and completes it with IsSuccess=false (ConnectionAborted) -- possibly BEFORE it raises
/// NavigationStarting for the new one. So the host marks the in-flight navigation abandoned right before
/// it navigates (<see cref="BeforeHostNavigate"/>); a completion of an abandoned id (exact match) is
/// superseded and consumed. Everything else, a stale success included only if abandoned, is handled as
/// before, so a real failure of the current navigation still tears the layer down.
/// </summary>
internal sealed class AlertLayerNavigation
{
    private readonly HashSet<ulong> _abandoned = [];
    private ulong? _inFlight;

    // The most recent NavigationStarting, kept after it completes: a completion of any OTHER id was
    // overtaken by a newer navigation (review R3-navigate-before-starting-race: two host Navigate calls
    // before the first start is delivered leave BeforeHostNavigate nothing to abandon).
    private ulong? _latestStarted;

    public void Started(ulong navigationId)
    {
        _inFlight = navigationId;
        _latestStarted = navigationId;
    }

    /// <summary>Call immediately before the host calls Navigate.</summary>
    public void BeforeHostNavigate()
    {
        if (_inFlight is { } id) _abandoned.Add(id);
        _inFlight = null;
    }

    /// <summary>True (and the id is consumed) when this completion belongs to an abandoned navigation.</summary>
    public bool CompletedIsSuperseded(ulong navigationId)
    {
        if (_abandoned.Remove(navigationId)) return true;
        if (_latestStarted is { } latest && latest != navigationId) return true;
        if (_inFlight == navigationId) _inFlight = null;
        return false;
    }

    public void Clear()
    {
        _abandoned.Clear();
        _inFlight = null;
        _latestStarted = null;
    }
}
