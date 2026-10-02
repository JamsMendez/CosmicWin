namespace CosmicWin.App.Tests;

/// <summary>
/// Covers F2 of <c>odd/tasks/video-endpoint-followups.md</c>: <see cref="AppComposition.MtaActionThread"/>'s
/// posted-work loop used to run <c>try { work(); } catch { }</c>, so a work item that threw after
/// the import (persist, attach, play) left no trace line at all. It now reports the exception TYPE
/// -- never <see cref="Exception.Message"/>, which can hold an absolute path -- through an injected
/// sink, and keeps serving later work either way.
/// </summary>
public sealed class AppCompositionMtaActionThreadTests
{
    [Fact]
    public void Run_WhenPostedWorkThrows_ReportsOnlyTheExceptionTypeAndKeepsServingLaterWork()
    {
        var reported = new List<string>();
        using var thread = new AppComposition.MtaActionThread(
            "AppCompositionMtaActionThreadTests", onWorkFailed: message => reported.Add(message));
        var secondItemRan = new ManualResetEventSlim(initialState: false);

        thread.Post(() => throw new IOException(@"C:\Users\x\secret.mp4"));
        thread.Post(() => secondItemRan.Set());

        Assert.True(secondItemRan.Wait(TimeSpan.FromMinutes(1)), "the second work item never ran"); // was 5s: hang guard sized for the 2-core GitHub Actions runner
        AssertEventually(() => Assert.Single(reported));
        Assert.Contains(nameof(IOException), reported[0]);
        Assert.DoesNotContain(@"C:\Users\x\secret.mp4", reported[0]);
    }

    /// <summary>
    /// Review R3-002: the "keeps serving" guarantee must not depend on the sink. A sink that throws
    /// must not let the exception escape Run, which would end the thread (and, unhandled on a
    /// background thread, the process).
    /// </summary>
    [Fact]
    public void Run_WhenTheFailureSinkItselfThrows_KeepsServingLaterWork()
    {
        using var thread = new AppComposition.MtaActionThread(
            "AppCompositionMtaActionThreadTests.ThrowingSink",
            onWorkFailed: _ => throw new InvalidOperationException("sink failed"));
        var secondItemRan = new ManualResetEventSlim(initialState: false);

        thread.Post(() => throw new IOException("work failed"));
        thread.Post(() => secondItemRan.Set());

        Assert.True(secondItemRan.Wait(TimeSpan.FromMinutes(1)), "the second work item never ran"); // was 5s: hang guard sized for the 2-core GitHub Actions runner
    }

    /// <summary>Waits briefly for an async assertion to stop failing, instead of racing the worker thread.</summary>
    private static void AssertEventually(Action assertion)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1); // was 5s: hang guard sized for the 2-core GitHub Actions runner
        while (true)
        {
            try
            {
                assertion();
                return;
            }
            catch (Exception) when (DateTimeOffset.UtcNow < deadline)
            {
                Thread.Sleep(10);
            }
        }
    }
}
