namespace CosmicWin.App.Alerts;

/// <summary>
/// One alert on screen: which command it is, and when its display window started, so the overlay
/// can animate from that instant.
/// </summary>
public sealed record ActiveAlert(AlertCommand Command, DateTimeOffset StartedAt);

/// <summary>
/// Decides which single parsed alert command -- if any -- is on screen or waiting to be, right now.
/// </summary>
/// <remarks>
/// <para>
/// Pure and time-free by design (plan &#167;4/&#167;6, T2): every method takes <c>now</c> from the
/// caller rather than reading the clock or owning a timer. <see cref="Advance"/> is
/// meant to be called once per tick, alongside whether the desktop is currently visible, by the
/// overlay driver T5/T6 add.
/// </para>
/// <para>
/// Busy-ignore, decided by the maintainer 2026-09-26 (alert-busy-ignore feature, superseding the
/// earlier FIFO-of-many design this class used to document here): at most ONE alert exists at a
/// time, showing or waiting -- never more. <see cref="Enqueue"/> judges "in progress" against
/// <paramref name="now"/> at call time, not against state only <see cref="Advance"/> clears: a new
/// request is ignored (still reported as accepted to the caller, per the maintainer's decision that
/// the pipe/HTTP reply never reveals the difference) only while an alert is CURRENTLY showing
/// (<c>now</c> has not yet reached its start plus duration) or CURRENTLY waiting and not expired.
/// A request arriving once the showing alert's window has elapsed, or once the waiting one has aged
/// past <see cref="_maxAge"/>, is accepted and queued even though nobody has called
/// <see cref="Advance"/> to notice that yet -- it shows on the very next <see cref="Advance"/> call.
/// Every ignore and every drop is reported through <see cref="_onDiagnostic"/> so the caller can log
/// which happened and why.
/// </para>
/// <para>
/// Queued-while-covered behaviour, decided by the maintainer 2026-09-23 (see the feature's task
/// file, "Decided: alerts while the desktop is covered"): an alert that arrives while a fullscreen
/// window covers the desktop is queued rather than shown or dropped outright. It starts once the
/// desktop is visible again, unless it has waited longer than <see cref="_maxAge"/> since it was
/// enqueued, in which case <see cref="Advance"/> drops it -- reporting the drop -- the first time it
/// notices, whether or not the desktop is visible at that moment. Exactly <c>maxAge</c> is still
/// eligible; only strictly past it is dropped. An alert already showing when the desktop becomes
/// covered keeps its own clock running: it simply ends on time, it is never paused or extended, and
/// <see cref="Advance"/> keeps returning it as the active alert regardless of visibility until then --
/// only STARTING a new one requires the desktop to be visible.
/// </para>
/// <para>
/// NOT thread-safe: <see cref="Enqueue"/> and <see cref="Advance"/> both read and mutate the same
/// internal queue with no locking, by design -- the caller (a single-threaded render tick) is
/// expected to serialize every call.
/// </para>
/// </remarks>
public sealed class AlertQueue
{
    /// <summary>Default bound on how many not-yet-started alerts can wait at once.</summary>
    public const int DefaultCapacity = 8;

    /// <summary>Default ceiling on how long a queued alert may wait, from enqueue time, before it is dropped unshown.</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromMinutes(5);

    private readonly int _capacity;
    private readonly TimeSpan _maxAge;
    private readonly Action<string> _onDiagnostic;
    private readonly Queue<(AlertCommand Command, DateTimeOffset EnqueuedAt)> _pending = new();

    private ActiveAlert? _current;

    /// <param name="capacity">Bound on the FIFO of not-yet-started alerts. Small values let tests exercise rejection without enqueuing 8 commands.</param>
    /// <param name="maxAge">
    /// How long a queued alert may wait before it is dropped unshown; defaults to <see
    /// cref="DefaultMaxAge"/> when omitted. <see cref="TimeSpan.Zero"/> is allowed (finding
    /// R3-queue-ctor-unvalidated) and means "must start on the same tick it was enqueued, or be
    /// dropped" -- <see cref="DropExpired"/>'s strictly-greater-than check still leaves an alert
    /// exactly at its max age eligible, so a zero max age does not make every enqueue pointless.
    /// </param>
    /// <param name="onDiagnostic">
    /// Told about every rejection and every drop, with a short message naming the reason, so the
    /// caller can log it. Never called with anything else, and never expected to throw. Defaults to
    /// a no-op, the same optional-delegate convention <c>AppComposition</c>'s <c>persistX</c>
    /// parameters use.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="capacity"/> is not positive (a queue that can hold nothing would just reject
    /// every alert, which is a construction error, not a runtime rejection), or the resolved
    /// <paramref name="maxAge"/> is negative (finding R3-queue-ctor-unvalidated).
    /// </exception>
    public AlertQueue(int capacity = DefaultCapacity, TimeSpan? maxAge = null, Action<string>? onDiagnostic = null)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");
        }

        var resolvedMaxAge = maxAge ?? DefaultMaxAge;
        if (resolvedMaxAge < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAge), resolvedMaxAge, "Max age must not be negative.");
        }

        _capacity = capacity;
        _maxAge = resolvedMaxAge;
        _onDiagnostic = onDiagnostic ?? (_ => { });
    }

    /// <summary>
    /// Queues <paramref name="command"/>, unless an alert is already showing or waiting at
    /// <paramref name="now"/> (alert-busy-ignore, maintainer decision 2026-09-26), in which case the
    /// request is IGNORED: reported through <see cref="_onDiagnostic"/> and dropped, but still
    /// answered as accepted -- the caller (<c>AppComposition.HandleAlertCommand</c>) cannot tell an
    /// ignored request apart from a queued one, by design, so the pipe/HTTP reply stays identical.
    /// </summary>
    /// <remarks>
    /// Drops any already-expired waiting alert first (reusing <see cref="DropExpired"/>, so that drop
    /// is still reported) before judging whether the queue is busy -- an alert nobody would ever
    /// start again must not swallow a new request. The capacity check below is kept as a defensive
    /// invariant guard, not because the composition can still reach it: with at most one alert ever
    /// queued through this busy-ignore rule, <c>_pending.Count</c> cannot grow past 1, so it can only
    /// equal <see cref="_capacity"/> when <c>capacity</c> itself is 1 -- a case the busy-ignore check
    /// above already intercepts first. See the feature's task file for that decision.
    /// </remarks>
    /// <returns>
    /// <see langword="true"/> when accepted -- whether newly queued or ignored because one is already
    /// in progress -- <see langword="false"/> only if the queue were ever at capacity despite that
    /// (currently unreachable in practice; see remarks).
    /// </returns>
    public bool Enqueue(AlertCommand command, DateTimeOffset now)
    {
        DropExpired(now);

        if (_current is { } active && now < active.StartedAt + active.Command.Duration)
        {
            _onDiagnostic("alert ignored: one is already showing");
            return true;
        }

        if (_pending.Count > 0)
        {
            _onDiagnostic("alert ignored: one is already waiting to show");
            return true;
        }

        if (_pending.Count >= _capacity)
        {
            _onDiagnostic($"alert rejected: queue is already at capacity ({_capacity})");
            return false;
        }

        _pending.Enqueue((command, now));
        return true;
    }

    /// <summary>
    /// Ends the current alert once its display window has elapsed, drops any queued alert that has
    /// waited past the max age, and -- if nothing is currently showing and the desktop is visible --
    /// starts the next eligible one.
    /// </summary>
    /// <returns>The alert that should be on screen right now, or <see langword="null"/> for none.</returns>
    public ActiveAlert? Advance(DateTimeOffset now, bool desktopVisible)
    {
        if (_current is { } active && now >= active.StartedAt + active.Command.Duration)
        {
            _current = null;
        }

        DropExpired(now);

        if (_current is not null)
        {
            return _current;
        }

        if (!desktopVisible || _pending.Count == 0)
        {
            return null;
        }

        var (command, _) = _pending.Dequeue();
        _current = new ActiveAlert(command, now);
        return _current;
    }

    /// <summary>
    /// Drops every alert at the front of the queue that has waited strictly longer than
    /// <see cref="_maxAge"/> since it was enqueued. Only the front needs checking: alerts are
    /// enqueued -- and therefore age -- in FIFO order, so nothing behind an eligible head can itself
    /// be expired.
    /// </summary>
    private void DropExpired(DateTimeOffset now)
    {
        while (_pending.Count > 0 && now - _pending.Peek().EnqueuedAt > _maxAge)
        {
            _pending.Dequeue();
            _onDiagnostic($"alert dropped: waited longer than the {_maxAge} max age without starting");
        }
    }
}
