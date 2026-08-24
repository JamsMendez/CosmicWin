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
        if (TryAcquire(timeout, out var held, name))
        {
            return held!;
        }

        throw new TimeoutException(
            $"Waited {timeout.TotalSeconds:0.#}s for '{name ?? Name}'. This is the test " +
            "harness, not the product: another test project's desktop collection is still holding " +
            "the real desktop; a holder that DIED is handed over at once. Run the desktop projects " +
            "one at a time rather than through the solution.");
    }

    /// <summary>Non-throwing form, so a fact can OBSERVE the lock being held instead of only feeling it.</summary>
    public static bool TryAcquire(TimeSpan timeout, out RealDesktopLock? held, string? name = null)
    {
        var release = new ManualResetEventSlim(false);
        var settled = new ManualResetEventSlim(false);
        var taken = false;

        var owner = new Thread(() =>
        {
            using var mutex = new Mutex(false, name ?? Name);
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

            settled.Set();
            if (taken)
            {
                release.Wait();
                mutex.ReleaseMutex();
            }
        })
        { IsBackground = true, Name = "cosmicwin-real-desktop-lock" };

        owner.Start();
        settled.Wait();
        settled.Dispose();

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
