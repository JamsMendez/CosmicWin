using CosmicWin.Interop.Win32;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// The style write and the restore are synchronous cross-process calls: <c>SetWindowLong</c> sends
/// <c>WM_STYLECHANGING</c>/<c>WM_STYLECHANGED</c> to the target, <c>SetWindowPos</c> with
/// <c>SWP_FRAMECHANGED</c> sends <c>WM_NCCALCSIZE</c>, and <c>ShowWindow(SW_RESTORE)</c> sends the
/// size messages. A hung target window therefore stalls the caller. These facts pin the bound that
/// stops it, headlessly, through the delegate seam shared with the activation bound.
/// </summary>
public sealed class BoundedStyleCallTests
{
    private static readonly TimeSpan LongEnoughForAnImmediateReturn = TimeSpan.FromSeconds(5);

    [Fact]
    public void RunStyleCall_WhenTheTargetNeverAnswers_ReturnsFalseWithinTheBudget()
    {
        // Not `using`: see BoundedActivationTests for why a parked worker must never see a disposed event.
        var release = new ManualResetEventSlim(false);
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();

            var applied = Win32NativeWindowSource.RunStyleCall(
                () =>
                {
                    release.Wait();
                    return true;
                },
                TimeSpan.FromMilliseconds(50));

            watch.Stop();
            Assert.False(applied);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"the call was not bounded: {watch.Elapsed}");
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public void RunStyleCall_WhenTheTargetAnswersInTime_ReportsItsAnswer()
    {
        Assert.True(Win32NativeWindowSource.RunStyleCall(() => true, LongEnoughForAnImmediateReturn));
        Assert.False(Win32NativeWindowSource.RunStyleCall(() => false, LongEnoughForAnImmediateReturn));
    }

    [Fact]
    public void RunStyleCall_WhenTheCallThrows_ReturnsFalseAndDoesNotCrashTheProcess()
    {
        var exception = Record.Exception(() =>
            Assert.False(Win32NativeWindowSource.RunStyleCall(
                () => throw new InvalidOperationException("boom"),
                LongEnoughForAnImmediateReturn)));

        Assert.Null(exception);
    }

    [Fact]
    public void RunBounded_Generic_ReportsTheTimeoutValueSeparatelyFromTheFailureValue()
    {
        var release = new ManualResetEventSlim(false);
        try
        {
            var hung = Win32NativeWindowSource.RunBounded(
                () =>
                {
                    release.Wait();
                    return "done";
                },
                TimeSpan.FromMilliseconds(50),
                whenFailed: "failed",
                whenTimedOut: "timed out");
            var threw = Win32NativeWindowSource.RunBounded<string>(
                () => throw new InvalidOperationException("boom"),
                LongEnoughForAnImmediateReturn,
                whenFailed: "failed",
                whenTimedOut: "timed out");

            Assert.Equal("timed out", hung);
            Assert.Equal("failed", threw);
        }
        finally
        {
            release.Set();
        }
    }
}
