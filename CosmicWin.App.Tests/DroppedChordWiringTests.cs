using System.Linq;
using System.Threading.Channels;
using CosmicWin.App.Input;
using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App.Tests;

/// <summary>
/// The reconciliation tick publishing a chord that MATCHED but was silently discarded because the
/// dispatcher channel was full.
/// </summary>
/// <remarks>
/// <para>
/// Seven days of trace evidence showed that when desktop chords "go dead" for seconds at a time,
/// NOTHING is written to the trace for the whole stretch. The cause traced to
/// <see cref="ChannelWriter{T}.TryWrite"/>: <c>ActionDispatcher</c>'s channel is bounded with
/// <c>BoundedChannelFullMode.Wait</c>, which reads as "a full write blocks" -- but <c>TryWrite</c>
/// is the one member on that channel that never blocks, and on a full channel it simply returns
/// <c>false</c>. The action a matched chord produced vanishes, and <c>RecordUnmatched</c> never
/// fires, because the chord matched.
/// </para>
/// <para>
/// This suite proves the WIRING half of the fix: that <see cref="AppComposition"/>'s reconciliation
/// tick actually reads <c>LowLevelKeyboardHook.DroppedChords</c>/<c>LastDroppedChord</c> and
/// publishes them to the desktop trace, on change only -- mirroring the existing
/// <c>unmatched chord</c> and <c>hook reinstalled by watchdog</c> lines it sits beside. The counting
/// itself is proven at the processor level, in a companion suite next to
/// <c>UnmatchedChordDiagnosticsTests</c>.
/// </para>
/// </remarks>
public sealed class DroppedChordWiringTests
{
    private sealed class NullDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }

    /// <summary>Captures the recurring reconciliation pass instead of running a real timer, matching the idiom already established in <c>AppCompositionTests</c> and <c>AppCompositionDesktopFocusWiringTests</c>.</summary>
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
        public void Record(string line) => lines.Add(line);
    }

    private sealed record Harness(
        AppComposition Composition, ImmediateScheduler Scheduler, FakeKeyboardHookPlatform Platform,
        List<string> Trace);

    /// <summary>
    /// One monitor, no windows -- the dropped-chord line is the only thing under test. The hook is
    /// wired to a CAPACITY-1 channel captured by the test, deliberately NOT the dispatcher's own
    /// 32-slot channel: forcing that one full would mean racing <c>ActionDispatcher.RunAsync</c>'s
    /// own pump for its 32 writes, and the point of this suite is a deterministic full channel, not
    /// a lucky one.
    /// </summary>
    private static Harness Wire()
    {
        var traceLines = new List<string>();
        var display = new FakeDisplay(
            new IntPtr(1), Rectangle.FromSize(0, 0, 1920, 1080), Rectangle.FromSize(0, 0, 1920, 1080), 1.0, true);
        var registry = new WindowRegistry();
        var treeManager = new TreeManager([display], display, registry);
        var scheduler = new ImmediateScheduler();
        var platform = new FakeKeyboardHookPlatform();
        var chordChannel = Channel.CreateBounded<HotkeyAction>(1);

        var composition = AppComposition.Wire(
            new FakeWorkspace(), treeManager, registry, new Foreground(),
            new ExceptionListStore(ExceptionList.Empty),
            focusTrace: new RecordingFocusTrace(),
            disableTaskTrigger: () => { },
            scheduleReconcile: scheduler.Schedule,
            // Ignores the writer AppComposition offers (the dispatcher's own channel) in favour of
            // the test's own capacity-1 one, for the reason on Wire's own summary above.
            hookFactory: _ => new LowLevelKeyboardHook(
                chordChannel.Writer, platform, TimeSpan.FromSeconds(5), () => 0),
            loadExceptions: () => ExceptionList.Empty,
            shutdown: () => { },
            buildTray: _ => new NullDisposable(),
            importVideoWallpaper: path => path,
            desktopTrace: new CollectingTrace(traceLines));

        return new Harness(composition, scheduler, platform, traceLines);
    }

    /// <summary>
    /// <see cref="FakeKeyboardHookPlatform.Install"/> fires one Alt+H the moment the hook starts,
    /// which <see cref="AppComposition.Wire"/> already does before this method returns -- so the
    /// capacity-1 channel is already full by the time a test gets hold of the harness, with no setup
    /// call of its own needed.
    /// </summary>
    [Fact]
    public void ATickAfterAMatchedChordWasDroppedForAFullChannel_WritesTheLineOnce()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            // Alt+L matches (FocusRight), but the channel is already full from the startup Alt+H --
            // so this one is dropped.
            var suppressed = harness.Platform.Raise(KeyboardKey.L, true, ModifierKeys.Alt);
            Assert.False(suppressed);

            harness.Scheduler.Fire();

            Assert.Single(
                harness.Trace,
                line => line == "chord dropped -- queue full: total=1 (+1 since last report) last=Alt+L");
        }
    }

    /// <summary>A tick where the total has not moved since the last report must not repeat the line.</summary>
    [Fact]
    public void ATickWhereNothingNewWasDropped_DoesNotRepeatTheLine()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            harness.Platform.Raise(KeyboardKey.L, true, ModifierKeys.Alt);
            harness.Scheduler.Fire();
            var afterFirstTick = harness.Trace.Count(
                line => line.StartsWith("chord dropped -- queue full", StringComparison.Ordinal));

            harness.Scheduler.Fire();

            var afterSecondTick = harness.Trace.Count(
                line => line.StartsWith("chord dropped -- queue full", StringComparison.Ordinal));
            Assert.Equal(1, afterFirstTick);
            Assert.Equal(afterFirstTick, afterSecondTick);
        }
    }
}
