namespace CosmicWin.App.Alerts;

/// <summary>
/// T9c (webview-alert-layer): the PURE state machine behind the permanently preloaded alert layer
/// (feature doc, "Idle cost (superseded 2026-09-24)") -- host identity/backoff tracking, readiness,
/// and the one pending show a caller may register before the page is ready to receive it.
/// </summary>
/// <remarks>
/// T6 measured ~2.8s from command to visible layer under the old create-per-alert model, and F1 (the
/// first alert after launch never showing at all). The maintainer's fix is to create ONE controller
/// at startup and keep it alive, showing/hiding it per alert instead of recreating it every time.
/// This class owns every decision that does not need a real WebView2 to make, so it is unit-tested
/// directly -- <see cref="WebViewAlertLayerController"/> is the thin WebView2-specific shell driven
/// by it (real environment/controller/navigation stay a hardware-only concern, same split T3/T6
/// already established for the old per-alert model).
/// <para>
/// Deliberately NOT the old <see cref="AlertLayerLifecycle"/>: that class auto-closes its resource
/// after a deadline elapses, which fit a controller created fresh per alert. A preloaded controller
/// must NOT be torn down when an alert's duration ends -- only <see cref="Failed"/> (creation/process
/// failure) or a host identity change ever calls for that, both handled by the controller itself.
/// </para>
/// </remarks>
public sealed class AlertLayerPreloadState(Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private bool _hostKnown;
    private nint _hwnd;
    private int _generation;
    private DateTimeOffset _retryAfter;
    private int _failures;
    private (string Kind, DateTimeOffset Deadline)? _pending;

    /// <summary>Environment + controller created, navigation completed, and the page's own "ready" message received.</summary>
    public bool Ready { get; private set; }

    /// <summary>The layer is currently supposed to be shown (<c>IsVisible = true</c> on the real controller).</summary>
    public bool Visible { get; private set; }

    /// <summary>Whether creation/recreation may be attempted right now -- false while backing off after <see cref="Failed"/>.</summary>
    public bool CanCreate => _clock() >= _retryAfter;

    /// <summary>
    /// True the FIRST time it is called (nothing was known yet), and again every time the host's
    /// hwnd/generation actually differs from what was last observed -- an Explorer restart, or a
    /// host that was just attached for the first time. False when the identity is unchanged.
    /// </summary>
    public bool HostChanged(nint hwnd, int generation)
    {
        if (_hostKnown && hwnd == _hwnd && generation == _generation) return false;
        _hostKnown = true;
        _hwnd = hwnd;
        _generation = generation;
        return true;
    }

    /// <summary>Creation or the live process failed: not ready, not visible, and back off exponentially before the next attempt (500ms, 1000ms, ... capped at 8000ms).</summary>
    public void Failed()
    {
        Ready = false;
        Visible = false;
        _retryAfter = _clock().AddMilliseconds(Math.Min(8000, 500 * (1 << Math.Min(_failures++, 4))));
    }

    /// <summary>A creation attempt succeeded well enough to proceed to navigation: resets the backoff so a LATER failure starts counting from the first step again.</summary>
    public void Created() => _failures = 0;

    /// <summary>The page's own "ready" message arrived after a completed navigation.</summary>
    public void MarkReady() => Ready = true;

    /// <summary>
    /// A start was requested. While ready, returns the kind/duration to post RIGHT NOW and marks the
    /// layer visible -- even if it was already visible (a Start while showing must still re-show, not
    /// be swallowed as a no-op; the queue, not this class, decides whether that Start was warranted).
    /// While not yet ready, remembers it as a pending show with an ABSOLUTE deadline and returns null;
    /// see <see cref="ApplyPendingShowIfDue"/>.
    /// </summary>
    public (string Kind, int DurationMilliseconds)? RequestShow(string kind, int durationMilliseconds)
    {
        if (Ready)
        {
            Visible = true;
            _pending = null;
            return (kind, durationMilliseconds);
        }

        _pending = (kind, _clock().AddMilliseconds(durationMilliseconds));
        return null;
    }

    /// <summary>
    /// Call once <see cref="MarkReady"/> has just made the page ready: applies a still-pending show
    /// by returning its REMAINING duration (never the original one), or drops it silently if its
    /// deadline already passed. Returns null when there was nothing to apply.
    /// </summary>
    public (string Kind, int DurationMilliseconds)? ApplyPendingShowIfDue()
    {
        if (_pending is not { } pending) return null;
        _pending = null;
        if (!Ready) return null;
        var remaining = pending.Deadline - _clock();
        if (remaining <= TimeSpan.Zero) return null;
        Visible = true;
        return (pending.Kind, (int)Math.Ceiling(remaining.TotalMilliseconds));
    }

    /// <summary>
    /// The queue ended the alert: hides the layer and drops any not-yet-applied pending show, WITHOUT
    /// touching <see cref="Ready"/> -- hiding a preloaded layer must never close/tear it down.
    /// </summary>
    public void Hide()
    {
        Visible = false;
        _pending = null;
    }

    /// <summary>The page signaled its own "done": the controller reports nothing else back -- the queue owns ending the alert (it calls <see cref="Hide"/> itself once it advances).</summary>
    public void PageDone() => Visible = false;
}
