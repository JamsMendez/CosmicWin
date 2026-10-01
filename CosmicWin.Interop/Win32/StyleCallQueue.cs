namespace CosmicWin.Interop.Win32;

/// <summary>
/// Bounded style calls where a call on a window never starts before an earlier call on the SAME
/// window has finished -- even one whose caller already gave up -- and where a window that never
/// answers parks at most one worker thread.
/// </summary>
/// <remarks>
/// <para>
/// A style write is a synchronous cross-process call that a hung target can park indefinitely, so
/// each runs under a budget and a timeout answers <see cref="StyleWriteOutcome.TimedOut"/>. The
/// worker cannot be cancelled, only abandoned, and it can still land later. That opens an ordering
/// race: a strip that timed out and a give-back issued afterwards are two writes to the same style,
/// and the give-back reads the box as still present (the strip has not landed), writes nothing, and
/// reports success -- then the strip lands and leaves a disabled button for good.
/// </para>
/// <para>
/// The fix is ordering, not a retry: per window, a call starts only after the previous one has
/// finished. The give-back therefore reads AFTER the strip landed, sees the box gone and puts it
/// back. The waiting never happens on the caller, so the caller (the UI/dispatcher thread) is still
/// bounded by <c>budget</c>: the queued call reports <see cref="StyleWriteOutcome.TimedOut"/> now
/// and its effect arrives when the window answers. Calls on different windows never wait for each
/// other.
/// </para>
/// <para>
/// The bound is real, not assumed. Per window there is at most ONE running call and ONE pending
/// (not yet started) call, and the pending one is held as data, not on a parked thread: when the
/// running call finishes, the same worker thread starts it. So a window that never answers costs one
/// parked thread however many callers pile up (repeated Alt+T, a give-back after a strip). A newer
/// pending call REPLACES the older one, which never runs and reports
/// <see cref="StyleWriteOutcome.TimedOut"/> to its waiter. That is only sound because every ordered
/// call is an idempotent "set the state to X" write where the latest wanted state is the only one
/// that matters; a strip superseded by a give-back simply never lands, and the box is present in the
/// end, as asked. Order between the running call and the pending one is kept, which is all the
/// give-back-after-strip guarantee needs.
/// </para>
/// <para>
/// <see cref="RunIndependent"/> is the lane for a call that must NOT wait for the ordered writes. A
/// restore from maximized only undoes a maximize; it has no ordering requirement against a maximize
/// box write (it touches the maximized state, the strip touches one other bit, and neither reads
/// what the other wrote to decide), so queuing it behind a hung strip would only turn it into a
/// false refusal that the adapter then counts toward evicting the window. It is bounded the other
/// way, by admission: one in flight per window, and a call that arrives while one is in flight is
/// answered <see cref="StyleWriteOutcome.TimedOut"/> without starting anything. A hung window thus
/// parks at most two threads in total, one per lane.
/// </para>
/// </remarks>
internal sealed class StyleCallQueue
{
    private sealed class Request(Func<bool> call)
    {
        public Func<bool> Call { get; } = call;

        public ManualResetEventSlim Done { get; } = new(false);

        public bool Applied { get; set; }

        public bool Superseded { get; set; }
    }

    private sealed class Lane
    {
        public Request? Pending { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<nint, Lane> _ordered = [];
    private readonly HashSet<nint> _independent = [];
    private int _workers;

    /// <summary>Worker threads alive right now, across both lanes. For tests: the parked-thread bound.</summary>
    internal int WorkerCount
    {
        get
        {
            lock (_gate)
            {
                return _workers;
            }
        }
    }

    /// <summary>Whether an ordered call on <paramref name="key"/> is waiting for the running one.</summary>
    internal bool HasPending(nint key)
    {
        lock (_gate)
        {
            return _ordered.TryGetValue(key, out var lane) && lane.Pending is not null;
        }
    }

    /// <summary>
    /// Runs <paramref name="call"/> after any earlier ordered call on <paramref name="key"/> has
    /// finished, waiting at most <paramref name="budget"/> for the answer. A throwing call is a
    /// refusal; a call replaced by a newer one before it started reports TimedOut and never runs.
    /// </summary>
    public StyleWriteOutcome Run(nint key, Func<bool> call, TimeSpan budget)
    {
        var request = new Request(call);
        var startWorker = false;
        lock (_gate)
        {
            if (_ordered.TryGetValue(key, out var lane))
            {
                if (lane.Pending is { } older)
                {
                    older.Superseded = true;
                    older.Done.Set();
                }

                lane.Pending = request;
            }
            else
            {
                _ordered[key] = new Lane();
                _workers++;
                startWorker = true;
            }
        }

        if (startWorker)
        {
            new Thread(() => Drain(key, request)) { IsBackground = true }.Start();
        }

        return Await(request, budget);
    }

    /// <summary>
    /// Runs <paramref name="call"/> on its own worker outside the ordered lane, at most one in flight
    /// per window; while one is in flight the call is answered TimedOut and nothing is started.
    /// </summary>
    public StyleWriteOutcome RunIndependent(nint key, Func<bool> call, TimeSpan budget)
    {
        lock (_gate)
        {
            if (!_independent.Add(key))
            {
                return StyleWriteOutcome.TimedOut;
            }

            _workers++;
        }

        var request = new Request(call);
        new Thread(() =>
        {
            Execute(request);
            lock (_gate)
            {
                _independent.Remove(key);
                _workers--;
            }
        })
        { IsBackground = true }.Start();

        return Await(request, budget);
    }

    private static StyleWriteOutcome Await(Request request, TimeSpan budget)
    {
        if (!request.Done.Wait(budget) || request.Superseded)
        {
            return StyleWriteOutcome.TimedOut;
        }

        return request.Applied ? StyleWriteOutcome.Applied : StyleWriteOutcome.Refused;
    }

    private static void Execute(Request request)
    {
        try
        {
            request.Applied = request.Call();
        }
        catch (Exception)
        {
            request.Applied = false;
        }
        finally
        {
            request.Done.Set();
        }
    }

    /// <summary>Runs the first request, then whatever is pending, until the lane is empty.</summary>
    private void Drain(nint key, Request first)
    {
        var current = first;
        while (true)
        {
            Execute(current);
            lock (_gate)
            {
                var lane = _ordered[key];
                if (lane.Pending is null)
                {
                    _ordered.Remove(key);
                    _workers--;
                    return;
                }

                current = lane.Pending;
                lane.Pending = null;
            }
        }
    }
}
