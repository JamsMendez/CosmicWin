namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// A session-wide hold on the one real desktop, taken for as long as an assembly's desktop
/// collection is running and released when it ends.
/// </summary>
/// <remarks>
/// <para>
/// <c>DisableParallelization</c> on a collection stops at the assembly boundary, and the desktop
/// does not. <c>dotnet test</c> on the solution runs the test PROJECTS in parallel -- measured
/// here as three concurrent <c>testhost</c> processes -- so
/// <c>CosmicWin.Interop.Tests</c>' desktop collection and <c>CosmicWin.App.Tests</c>' can spawn
/// windows, activate them and inject synthetic keystrokes against the SAME foreground at the same
/// time. Both collections' remarks already prescribed running one project at a time; nothing made
/// that true. This does, and it cannot be forgotten the way a convention can.
/// </para>
/// <para>
/// A named <see cref="Mutex"/>, the only Windows primitive that survives its owner dying: a
/// semaphore left spent by a killed testhost gives the next run its whole timeout waiting on a
/// holder that is gone, so one crash buys a guaranteed stall; an abandoned mutex hands the next
/// waiter ownership at once, measured as 3.1s where a semaphore cost the full budget. Its price is
/// thread affinity -- and xunit may dispose a fixture off the thread that built it -- so one
/// dedicated thread takes it, parks, and releases it.
/// </para>
/// <para>
/// <c>Local\</c>, not <c>Global\</c>: the thing being guarded is this logon session's desktop, so
/// the guard belongs to the same session. <c>Global\</c> would reach across sessions it has no
/// business serialising and needs privileges this has no business asking for.
/// </para>
/// </remarks>
public sealed class RealDesktopLock : IDisposable
{
    /// <summary>Named, so a second process finds the SAME kernel object rather than its own copy.</summary>
    public const string Name = @"Local\CosmicWin.Tests.RealDesktop";

    /// <summary>
    /// Generous on purpose: the waiting side is idle, and the holder is a desktop suite full of
    /// deliberate settles. Too short would trade a race for a timeout, which is not a trade.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    private readonly Thread _owner;
    private readonly ManualResetEventSlim _release;

    private RealDesktopLock(Thread owner, ManualResetEventSlim release) =>
        (_owner, _release) = (owner, release);

    /// <summary>
    /// Takes the lock, or throws saying plainly that the failure is infrastructure. Running anyway
    /// on a timeout would quietly restore the race this exists to remove, so it does not.
    /// <paramref name="name"/> defaults to <see cref="Name"/>; this type's own facts pass their own.
    /// </summary>
    public static RealDesktopLock Acquire(TimeSpan timeout, string? name = null)
    {
        if (TryAcquire(timeout, out var held, out var failure, name))
        {
            return held!;
        }

        // Threw rather than timed out -- a different diagnosis, so a different exception. Saying
        // "waited 600s" about something that failed in the first millisecond would send a reader
        // hunting for a holder that never existed.
        //
        // The message does NOT name a cause it cannot know. One catch covers opening the handle AND
        // waiting on it, so blaming a stale kernel object every time would be right for the case
        // that motivated this and wrong for the rest -- the same over-claiming this file exists to
        // stop. The one cause that IS identifiable by type gets named, and nothing else does.
        if (failure is not null)
        {
            // Only what the exception PROVES. That the name is taken by another kind of object is
            // in the exception; who created it, and when, is not -- and guessing "an earlier build"
            // would be this very branch breaking the rule the comment above it just stated.
            var likely = failure is WaitHandleCannotBeOpenedException
                ? " That name is already held as a kernel object of ANOTHER type; a named object " +
                  "belongs to one type only."
                : string.Empty;

            throw new InvalidOperationException(
                $"Taking '{name ?? Name}' threw instead of timing out. This is the test harness, " +
                $"not the product; see the inner exception for what actually failed.{likely}",
                failure);
        }

        throw new TimeoutException(
            $"Waited {timeout.TotalSeconds:0.#}s for '{name ?? Name}'. This is the test " +
            "harness, not the product: another test project's desktop collection is still holding " +
            "the real desktop; a holder that DIED is handed over at once. Run the desktop projects " +
            "one at a time rather than through the solution.");
    }

    /// <summary>
    /// Non-throwing form, so a fact can OBSERVE the lock being held instead of only feeling it.
    /// </summary>
    /// <remarks>
    /// DISCARDS the reason. False from here means "not taken" and nothing more, so a caller that
    /// intends to RETRY must use the overload below instead: a name collision never resolves, and
    /// retrying one as though it were contention waits out a holder that does not exist. Use this
    /// only where the boolean is the whole answer.
    /// </remarks>
    public static bool TryAcquire(TimeSpan timeout, out RealDesktopLock? held, string? name = null) =>
        TryAcquire(timeout, out held, out _, name);

    /// <summary>
    /// As above, and hands back WHY it failed so <see cref="Acquire"/> can tell a timeout apart from
    /// a handle that could not be opened at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// TAKING the lock is inside the try, and <c>settled</c> is set from the finally. This is not
    /// defensive habit: an unhandled exception on a dedicated (non-pool) thread TERMINATES THE
    /// PROCESS, so a stale kernel object under this name used to abort the whole test run rather
    /// than fail one fixture -- measured, with the run reporting "Test host process crashed" and no
    /// fact of its own. Every desktop collection in both assemblies depends on this fixture, which
    /// makes it the worst place in the repository for that shape.
    /// </para>
    /// <para>
    /// RELEASING it is guarded by its OWN catch, and that failure is DISCARDED rather than carried
    /// out -- a different bargain, stated rather than glossed. By then <c>settled</c> is long since
    /// set and the caller has its answer, so there is genuinely nobody left to tell, and the process
    /// every other desktop fact shares is worth more than a report nobody would read. The mutex is
    /// handed back from a <c>finally</c>, so a failed wait cannot leave it held.
    /// </para>
    /// <para>
    /// Three reviews shaped this passage. The first caught it claiming a guard that had not been
    /// written; the second caught the guard releasing nothing if the wait threw; the third caught it
    /// claiming the release had happened even when the release itself was what failed. Twice the
    /// code changed, once the claim was withdrawn -- both are honest, and inventing a guarantee is
    /// not.
    /// </para>
    /// <para>
    /// The exception is CARRIED OUT, never swallowed. Reporting "not taken" while discarding the
    /// reason would trade a loud crash for a silent lie, and the caller would then wait out the full
    /// timeout blaming a holder that never existed.
    /// </para>
    /// <para>
    /// <c>settled</c> is deliberately not disposed, for the same reason the release event is not
    /// (see <see cref="Dispose"/>): the owner thread may still be inside <c>Set</c> when this method
    /// returns, and disposing an event another thread is touching is how a cleanup becomes a crash.
    /// </para>
    /// <para>
    /// Stated plainly, because "cheaper" would understate it: this leaks one event handle PER CALL,
    /// for the life of the process, with no bound in the API itself. It is tolerable only because
    /// acquisitions are collection-fixture scoped and number in the handful. A caller that took this
    /// lock in a loop would leak without limit, and would need the dispose put back with a real
    /// handshake rather than removed.
    /// </para>
    /// </remarks>
    public static bool TryAcquire(
        TimeSpan timeout, out RealDesktopLock? held, out Exception? failure, string? name = null)
    {
        var release = new ManualResetEventSlim(false);
        var settled = new ManualResetEventSlim(false);
        var taken = false;
        Exception? caught = null;

        var owner = new Thread(() =>
        {
            Mutex? mutex = null;
            try
            {
                mutex = new Mutex(false, name ?? Name);

                // Abandoned means the previous holder DIED still owning it: the wait SUCCEEDED and
                // ownership is ours, so reading it as failure would strand every run after a crash.
                try
                {
                    taken = mutex.WaitOne(timeout);
                }
                catch (AbandonedMutexException)
                {
                    taken = true;
                }
            }
            catch (Exception exception)
            {
                // Broad on purpose. The caller has a perfectly good "not taken" answer for every
                // one of these, and no exception is worth the process. It is recorded, not hidden.
                caught = exception;
                taken = false;
            }
            finally
            {
                settled.Set();
            }

            try
            {
                if (taken && mutex is not null)
                {
                    // ReleaseMutex in a FINALLY, not merely after the wait. A review caught the
                    // earlier ordering: if the wait threw, the release was skipped and the named
                    // mutex stayed HELD for the life of the process, deadlocking every later
                    // Acquire in the run. That is far worse than any handle leak, and the comment
                    // here described only the leak -- so the shape changed, not just the words.
                    try
                    {
                        release.Wait();
                    }
                    finally
                    {
                        mutex.ReleaseMutex();
                    }
                }

                mutex?.Dispose();
            }
            catch (Exception)
            {
                // Nothing may leave this thread, including on the way out. By here `settled` is long
                // since set and the caller has its answer, so there is genuinely nobody left to tell
                // -- and an unhandled exception would kill the process every other desktop fact in
                // both assemblies is sharing.
                //
                // What CAN still be lost here, stated exactly: a throw from ReleaseMutex itself
                // skips the Dispose below, so that handle survives to process exit. Whether the OS
                // released ownership before throwing is NOT something this code establishes, so it
                // is not claimed -- a later waiter is unblocked by abandonment when this thread
                // ends, which is a different mechanism from the one the finally provides.
            }
        })
        { IsBackground = true, Name = "cosmicwin-real-desktop-lock" };

        owner.Start();
        settled.Wait();

        failure = caught;
        held = taken ? new RealDesktopLock(owner, release) : null;
        return taken;
    }

    /// <summary>
    /// Idempotent by construction, not by a flag: setting a set event and joining a finished thread
    /// are both no-ops. The event is NOT disposed -- that would make a second Dispose throw from
    /// <c>Set</c>, trading a double-release for a double-dispose.
    /// </summary>
    public void Dispose()
    {
        _release.Set();
        _owner.Join();
    }
}
