namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// The stray sweep runs from inside <c>Spawn</c>'s catch block, on the way to rethrowing the
/// exception that explains why the spawn gave up. Anything it lets escape does not merely fail the
/// cleanup: it REPLACES that exception, and the maintainer is left reading about an enumeration
/// error instead of the timeout that actually happened.
/// </summary>
/// <remarks>
/// These facts need no desktop. The sweep takes its process enumeration as a delegate precisely so
/// the failure can be forced here, at the cheapest level that proves the contract, instead of being
/// argued about from the shape of the code.
/// </remarks>
public sealed class SpawnedNotepadWindowSweepTests
{
    /// <summary>
    /// A grace worth many passes of the sweep's own 100ms poll, not just over one of them.
    /// </summary>
    /// <remarks>
    /// <c>Thread.Sleep</c> promises a floor, never a ceiling. A grace of 150ms against a 100ms poll
    /// leaves 50ms of headroom, so one sleep landing late under a loaded CI agent ends the loop
    /// after a single pass and fails the assertion below for a reason that has nothing to do with
    /// the behaviour under test. That is the exact species of flaky desktop fact this file exists to
    /// help retire -- writing another one here would be self-defeating.
    /// </remarks>
    private static readonly TimeSpan GraceWorthManyPasses = TimeSpan.FromSeconds(1);

    [Fact]
    public void KillNotepadsStartedSince_WhenEnumeratingProcessesThrows_NeverLetsItEscape()
    {
        var attempts = 0;

        var escaped = Record.Exception(() => SpawnedNotepadWindow.KillNotepadsStartedSince(
            [],
            () =>
            {
                attempts++;
                throw new InvalidOperationException("the process table refused to be enumerated");
            },
            TimeSpan.Zero));

        Assert.Null(escaped);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void KillNotepadsStartedSince_WhenOneEnumerationThrows_KeepsSweepingForTheRestOfTheGrace()
    {
        var attempts = 0;

        SpawnedNotepadWindow.KillNotepadsStartedSince(
            [],
            () =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new InvalidOperationException("transient failure on the first pass");
                }

                return [];
            },
            GraceWorthManyPasses);

        // A sweep that gave up on its first bad pass would leave the window it was launched to
        // clean up alive, which is the whole defect it exists to prevent.
        Assert.True(attempts >= 2, $"expected the sweep to carry on after a failed enumeration, saw {attempts} attempt(s)");
    }
}
