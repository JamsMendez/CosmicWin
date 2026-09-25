namespace CosmicWin.App.Alerts;

/// <summary>
/// One alert on screen: which command it is, and when its display window started, so the overlay
/// can animate from that instant.
/// </summary>
public sealed record ActiveAlert(AlertCommand Command, DateTimeOffset StartedAt);

/// <summary>
/// Bounded FIFO of parsed alert commands, deciding which one -- if any -- is on screen right now.
/// </summary>
/// <remarks>
/// <para>
/// Pure and time-free by design (plan &#167;4/&#167;6, T2): every method takes <c>now</c> from the
/// caller rather than reading the clock or owning a timer. <see cref="Advance"/> is
/// meant to be called once per tick, alongside whether the desktop is currently visible, by the
/// overlay driver T5/T6 add.
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
    /// Queues <paramref name="command"/> if there is room. When the queue is already at
    /// <see cref="_capacity"/>, the NEW command is the one rejected -- an older, already-queued
    /// alert is never silently dropped to make room.
    /// </summary>
    /// <returns><see langword="true"/> when accepted, <see langword="false"/> when the queue was full.</returns>
    public bool Enqueue(AlertCommand command, DateTimeOffset now)
    {
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
