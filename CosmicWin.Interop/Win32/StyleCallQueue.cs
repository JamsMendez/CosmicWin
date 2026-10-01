namespace CosmicWin.Interop.Win32;

/// <summary>
/// Bounded style calls, one dedicated worker thread each, where a call on a window never starts
/// before an earlier call on the SAME window has finished -- even one whose caller already gave up.
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
/// The fix is ordering, not a retry: per window, each call waits on the completion of the previous
/// one before it touches the style. The give-back therefore reads AFTER the strip landed, sees the
/// box gone and puts it back. The waiting happens on the call's own worker, never on the caller,
/// so the caller (the UI/dispatcher thread) is still bounded by <c>budget</c> and nothing needs
/// marshalling back: the queued call reports <see cref="StyleWriteOutcome.TimedOut"/> now and its
/// effect arrives when the window answers. Calls on different windows never wait for each other.
/// </para>
/// <para>
/// Cost, accepted: a window that never answers parks one thread per queued call on it. The adapter
/// issues at most a strip and a give-back per window, so that is a handful of idle background
/// threads for a window that is already hung.
/// </para>
/// </remarks>
internal sealed class StyleCallQueue
{
    private readonly object _gate = new();
    private readonly Dictionary<nint, Task> _tails = [];

    /// <summary>
    /// Runs <paramref name="call"/> after any earlier call on <paramref name="key"/> has finished,
    /// waiting at most <paramref name="budget"/> for the answer. A throwing call is a refusal.
    /// </summary>
    public StyleWriteOutcome Run(nint key, Func<bool> call, TimeSpan budget)
    {
        var finished = new TaskCompletionSource();
        Task? previous;
        lock (_gate)
        {
            _tails.TryGetValue(key, out previous);
            _tails[key] = finished.Task;
        }

        var applied = false;
        var worker = new Thread(() =>
        {
            try
            {
                // Completed only through SetResult below, so waiting can never throw.
                previous?.Wait();
                applied = call();
            }
            catch (Exception)
            {
                applied = false;
            }
            finally
            {
                finished.SetResult();
                lock (_gate)
                {
                    if (_tails.TryGetValue(key, out var tail) && tail == finished.Task)
                    {
                        _tails.Remove(key);
                    }
                }
            }
        })
        {
            IsBackground = true,
        };
        worker.Start();

        if (!worker.Join(budget))
        {
            return StyleWriteOutcome.TimedOut;
        }

        return applied ? StyleWriteOutcome.Applied : StyleWriteOutcome.Refused;
    }
}
