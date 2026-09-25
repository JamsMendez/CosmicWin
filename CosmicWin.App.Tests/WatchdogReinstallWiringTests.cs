using System.Linq;
using System.Threading.Channels;
using CosmicWin.App.Input;
using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App.Tests;

/// <summary>
/// The reconciliation tick publishing the <c>hook reinstalled by watchdog</c> line.
/// </summary>
/// <remarks>
/// <para>
/// Windows silently uninstalls a low-level keyboard hook whose callback overruns
/// <c>LowLevelHooksTimeout</c>, and the watchdog puts it back. That line is the only record a dead
/// stretch of chords caused by a ghosted hook leaves, and <c>foundGone</c> is what tells a rescue
/// apart from a reinstall that tore down a hook Windows was still holding. Until this suite, no test
/// asserted it: the string only appeared in <see cref="DroppedChordWiringTests"/>'s doc comment.
/// </para>
/// <para>
/// Same harness shape as <see cref="DroppedChordWiringTests"/>, plus a clock the test owns. The
/// watchdog fires when <em>its</em> clock says the hook has been silent for the interval, so moving
/// that clock forward makes exactly one reinstall happen, without sleeping and without a real hook.
/// </para>
/// </remarks>
public sealed class WatchdogReinstallWiringTests
{
    private const string LinePrefix = "hook reinstalled by watchdog";

    private sealed class NullDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class ImmediateScheduler
    {
        private Action? _callback;

        public IDisposable Schedule(TimeSpan interval, Action callback)
        {
            _callback = callback;
            return new NullDisposable();
        }

        public void Fire() => _callback!();
    }

    private sealed class Foreground : IForegroundWindowSource
    {
        public nint GetForegroundHandle() => 0;
    }

    private sealed class CollectingTrace(List<string> lines) : CosmicWin.App.Diagnostics.IDesktopTrace
    {
        public void Record(string line)
        {
            lock (lines) lines.Add(line);
        }
    }

    /// <summary>A millisecond clock the test moves by hand; read from the hook's own thread.</summary>
    private sealed class ManualClock
    {
        private long _now;

        public long Read() => Interlocked.Read(ref _now);

        public void Advance(long milliseconds) => Interlocked.Add(ref _now, milliseconds);
    }

    private sealed record Harness(
        AppComposition Composition, ImmediateScheduler Scheduler, FakeKeyboardHookPlatform Platform,
        ManualClock Clock, Func<LowLevelKeyboardHook> Hook, List<string> Trace)
    {
        public string[] WatchdogLines()
        {
            lock (Trace) return Trace.Where(line => line.StartsWith(LinePrefix, StringComparison.Ordinal)).ToArray();
        }
    }

    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// One monitor, no windows. <paramref name="hookStillInstalled"/> is what the platform's unhook
    /// reports when the watchdog replaces the hook, which is exactly what <c>foundGone</c> counts.
    /// </summary>
    private static Harness Wire(bool hookStillInstalled)
    {
        var traceLines = new List<string>();
        var display = new FakeDisplay(
            new IntPtr(1), Rectangle.FromSize(0, 0, 1920, 1080), Rectangle.FromSize(0, 0, 1920, 1080), 1.0, true);
        var registry = new WindowRegistry();
        var treeManager = new TreeManager([display], display, registry);
        var scheduler = new ImmediateScheduler();
        var platform = new FakeKeyboardHookPlatform { UninstallResult = hookStillInstalled };
        var clock = new ManualClock();
        LowLevelKeyboardHook? hook = null;

        var composition = AppComposition.Wire(
            new FakeWorkspace(), treeManager, registry, new Foreground(),
            new ExceptionListStore(ExceptionList.Empty),
            focusTrace: new RecordingFocusTrace(),
            disableTaskTrigger: () => { },
            scheduleReconcile: scheduler.Schedule,
            hookFactory: writer => hook = new LowLevelKeyboardHook(writer, platform, WatchdogInterval, clock.Read),
            loadExceptions: () => ExceptionList.Empty,
            shutdown: () => { },
            buildTray: _ => new NullDisposable(),
            importVideoWallpaper: path => path,
            desktopTrace: new CollectingTrace(traceLines));

        return new Harness(composition, scheduler, platform, clock, () => hook!, traceLines);
    }

    /// <summary>
    /// Makes the watchdog reinstall exactly once: the fake platform starts the hook with one Alt+H,
    /// so moving the clock past the interval (with no newer key and no cursor movement) is silence
    /// the watchdog cannot explain.
    /// </summary>
    private static void ForceOneReinstall(Harness harness)
    {
        harness.Clock.Advance((long)WatchdogInterval.TotalMilliseconds + 1);
        Assert.True(harness.Platform.SecondInstall.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(SpinWait.SpinUntil(() => harness.Hook().WatchdogReinstalls == 1, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void ATickAfterTheWatchdogReplacedAHookWindowsStillHeld_WritesTheLineWithFoundGoneZero()
    {
        var harness = Wire(hookStillInstalled: true);
        using (harness.Composition)
        {
            ForceOneReinstall(harness);

            harness.Scheduler.Fire();

            Assert.Equal(
                ["hook reinstalled by watchdog -- total=1 (+1 since last report) foundGone=0"],
                harness.WatchdogLines());
        }
    }

    [Fact]
    public void ATickAfterTheWatchdogRescuedAHookWindowsHadDropped_WritesTheLineWithFoundGoneOne()
    {
        var harness = Wire(hookStillInstalled: false);
        using (harness.Composition)
        {
            ForceOneReinstall(harness);

            harness.Scheduler.Fire();

            Assert.Equal(
                ["hook reinstalled by watchdog -- total=1 (+1 since last report) foundGone=1"],
                harness.WatchdogLines());
        }
    }

    [Fact]
    public void ATickWhereTheWatchdogDidNothingNew_DoesNotRepeatTheLine()
    {
        var harness = Wire(hookStillInstalled: true);
        using (harness.Composition)
        {
            ForceOneReinstall(harness);
            harness.Scheduler.Fire();

            harness.Scheduler.Fire();

            Assert.Single(harness.WatchdogLines());
        }
    }

    [Fact]
    public void ATickBeforeTheWatchdogEverFired_WritesNoLine()
    {
        var harness = Wire(hookStillInstalled: true);
        using (harness.Composition)
        {
            harness.Scheduler.Fire();

            Assert.Empty(harness.WatchdogLines());
        }
    }
}
