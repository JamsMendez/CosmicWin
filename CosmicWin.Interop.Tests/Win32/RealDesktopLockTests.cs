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
}
