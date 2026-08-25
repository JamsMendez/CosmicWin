namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// The one real desktop is shared across PROCESSES, so the lock that guards it has to be too.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RealDesktopCollection"/> already carries <c>DisableParallelization</c>, and its own
/// remarks say what that cannot do: it serialises within ONE assembly, while <c>dotnet test</c> on
/// the solution runs the test PROJECTS in parallel. Measured on this machine, a solution-wide run
/// puts three <c>testhost</c> processes on the CPU at once. Nothing in the repository enforced the
/// one-project-at-a-time discipline that remark prescribes -- there is no <c>.runsettings</c>, no
/// <c>xunit.runner.json</c>, and neither the README nor CI mentions it -- so the requirement was
/// real and the mechanism to meet it was a sentence in a comment.
/// </para>
/// <para>
/// These facts are headless on purpose and carry no <c>RequiresDesktop</c> trait: they prove the
/// LOCK, not anything about windows. The second handle is opened in this same process, which is a
/// faithful stand-in for another process rather than a weaker one -- a named mutex is a kernel
/// object keyed by its name, so a second open finds the SAME object and blocks on the kernel's
/// count, exactly as a second testhost would. What it does not prove is the cross-process case
/// end to end; that is left to an actual two-project run.
/// </para>
/// <para>
/// They take a lock of their OWN, under <see cref="FactsOnlyName"/>, never
/// <see cref="RealDesktopLock.Name"/>. This class is NOT in the serialised collection and must not
/// be -- that fixture holds the production lock, so a fact waiting on it would wait on itself -- so
/// xunit may schedule these beside a live desktop run, where the production name would fail them.
/// </para>
/// </remarks>
public sealed class RealDesktopLockTests
{
    /// <summary>Never <see cref="RealDesktopLock.Name"/> -- see this class's remarks.</summary>
    private const string FactsOnlyName = @"Local\CosmicWin.Tests.RealDesktopLockFacts";

    /// <summary>Long enough to be a real wait, short enough that a broken lock fails fast.</summary>
    private static readonly TimeSpan Brief = TimeSpan.FromMilliseconds(250);

    [Fact]
    public void WhileOneRunHoldsIt_AnotherCannotTakeIt()
    {
        using var held = RealDesktopLock.Acquire(Brief, FactsOnlyName);

        Assert.False(
            RealDesktopLock.TryAcquire(Brief, out var second, FactsOnlyName),
            "A second holder got the lock while the first still had it, so two desktop suites " +
            "could drive the one foreground at the same time.");
        Assert.Null(second);
    }

    /// <summary>
    /// Held for the whole collection and released at its end -- a lock that never came back would
    /// turn every later desktop run into a timeout.
    /// </summary>
    [Fact]
    public void OnceTheHolderIsDone_TheNextRunCanTakeIt()
    {
        using (RealDesktopLock.Acquire(Brief, FactsOnlyName))
        {
        }

        Assert.True(
            RealDesktopLock.TryAcquire(Brief, out var next, FactsOnlyName),
            "The lock was not released when its holder was disposed.");

        next!.Dispose();
    }

    /// <summary>
    /// A mutex is released only by the THREAD that took it, and xunit may dispose a fixture off the
    /// thread that built it -- which is why one dedicated thread owns it end to end. Taken on one
    /// thread here, released from a third, and neither throws.
    /// </summary>
    [Fact]
    public void TheHoldSurvivesTheThreadThatTookIt()
    {
        RealDesktopLock? held = null;
        var taker = new Thread(() => held = RealDesktopLock.Acquire(Brief, FactsOnlyName));
        taker.Start();
        taker.Join();

        Assert.False(RealDesktopLock.TryAcquire(Brief, out _, FactsOnlyName));

        var releaser = new Thread(() => held!.Dispose());
        releaser.Start();
        releaser.Join();

        Assert.True(RealDesktopLock.TryAcquire(Brief, out var afterwards, FactsOnlyName));
        afterwards!.Dispose();
    }

    /// <summary>
    /// A timeout is infrastructure, not a product defect, and has to say so out loud. Swallowing it
    /// and running anyway would quietly restore the very race this lock exists to remove.
    /// </summary>
    [Fact]
    public void WhenTheWaitRunsOut_AcquireSaysWhoseFaultItIsNot()
    {
        using var held = RealDesktopLock.Acquire(Brief, FactsOnlyName);

        var thrown = Assert.Throws<TimeoutException>(() => RealDesktopLock.Acquire(Brief, FactsOnlyName));

        Assert.Contains(FactsOnlyName, thrown.Message);
    }

    /// <summary>
    /// A named kernel object belongs to ONE type. If anything already holds this name as a
    /// semaphore, <c>new Mutex(false, name)</c> throws <see cref="WaitHandleCannotBeOpenedException"/>
    /// -- and that construction runs on the lock's own dedicated thread.
    /// </summary>
    /// <remarks>
    /// An unhandled exception on a dedicated (non-pool) thread TERMINATES THE PROCESS. So a stale
    /// object under this name does not fail one fixture: it kills the testhost, taking every desktop
    /// fact in BOTH assemblies with it, and reports nothing a reader could act on. The fixture that
    /// holds this lock is the one thing every desktop collection depends on, which makes it the
    /// worst possible place for that shape.
    /// <para>
    /// The squatter is a semaphore rather than a mutex on purpose. A second MUTEX of the same name
    /// is the ordinary contended case these facts already cover; a semaphore is a type collision,
    /// which is what a stale process from a pre-correction build would actually leave behind.
    /// </para>
    /// </remarks>
    [Fact]
    public void WhenTheNameIsHeldByAnotherKindOfObject_ItFailsToTakeTheLockInsteadOfKillingTheRun()
    {
        const string collidingName = FactsOnlyName + ".TypeCollision";
        using var squatter = new Semaphore(1, 1, collidingName);

        Assert.False(
            RealDesktopLock.TryAcquire(Brief, out var held, collidingName),
            "A name already held as a semaphore cannot be opened as a mutex, so the lock must " +
            "report that it was not taken.");
        Assert.Null(held);
    }

    /// <summary>
    /// The reason must reach the caller. A lock that answered "not taken" while discarding WHY
    /// would send a reader hunting for a holder that never existed, which is a quieter version of
    /// the same failure to communicate as the crash above.
    /// </summary>
    [Fact]
    public void WhenTheHandleCannotBeOpened_AcquireSaysSoInsteadOfBlamingATimeout()
    {
        const string collidingName = FactsOnlyName + ".TypeCollisionReported";
        using var squatter = new Semaphore(1, 1, collidingName);

        var thrown = Assert.ThrowsAny<Exception>(
            () => RealDesktopLock.Acquire(Brief, collidingName));

        // Never a TimeoutException: nothing was waited for, and saying otherwise would describe a
        // wait that did not happen.
        Assert.IsNotType<TimeoutException>(thrown);
        Assert.IsType<WaitHandleCannotBeOpenedException>(thrown.InnerException);
        Assert.Contains(collidingName, thrown.Message);
    }
}
