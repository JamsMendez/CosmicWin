using System.Linq;
using CosmicWin.App.Input;
using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App.Tests;

/// <summary>
/// The reconciliation tick publishing the <c>unmatched chord</c> line.
/// </summary>
/// <remarks>
/// <para>
/// A chord that matches nothing vanishes without trace, which is how "the right Alt does not
/// work" was once reported with no way to see what modifiers arrived. The hook keeps the last
/// failure in memory (<c>LowLevelKeyboardHook.LastUnmatchedChord</c>, proven at the processor level
/// in <c>UnmatchedChordDiagnosticsTests</c>), and only this tick writes it out. Until this suite no
/// test asserted that it does: the string only appeared in <see cref="DroppedChordWiringTests"/>'s
/// doc comment.
/// </para>
/// <para>
/// Same harness shape as <see cref="DroppedChordWiringTests"/>. The hook's processor always reads
/// the REAL physical modifier keys for the <c>raw=[...]</c> half (it is not injectable), so the
/// assertions pin everything up to <c>raw=[</c> and the repeat suffix, never what is inside.
/// </para>
/// </remarks>
public sealed class UnmatchedChordWiringTests
{
    private const string LinePrefix = "unmatched chord";

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

    private sealed record Harness(
        AppComposition Composition, ImmediateScheduler Scheduler, FakeKeyboardHookPlatform Platform,
        List<string> Trace)
    {
        public string[] UnmatchedLines()
        {
            lock (Trace) return Trace.Where(line => line.StartsWith(LinePrefix, StringComparison.Ordinal)).ToArray();
        }
    }

    /// <summary>One monitor, no windows. The watchdog interval is far longer than any test, so it never reinstalls mid-test.</summary>
    private static Harness Wire()
    {
        var traceLines = new List<string>();
        var display = new FakeDisplay(
            new IntPtr(1), Rectangle.FromSize(0, 0, 1920, 1080), Rectangle.FromSize(0, 0, 1920, 1080), 1.0, true);
        var registry = new WindowRegistry();
        var treeManager = new TreeManager([display], display, registry);
        var scheduler = new ImmediateScheduler();
        var platform = new FakeKeyboardHookPlatform();

        var composition = AppComposition.Wire(
            new FakeWorkspace(), treeManager, registry, new Foreground(),
            new ExceptionListStore(ExceptionList.Empty),
            focusTrace: new RecordingFocusTrace(),
            disableTaskTrigger: () => { },
            scheduleReconcile: scheduler.Schedule,
            hookFactory: writer => new LowLevelKeyboardHook(writer, platform, TimeSpan.FromHours(1)),
            loadExceptions: () => ExceptionList.Empty,
            shutdown: () => { },
            buildTray: _ => new NullDisposable(),
            importVideoWallpaper: path => path,
            desktopTrace: new CollectingTrace(traceLines));

        return new Harness(composition, scheduler, platform, traceLines);
    }

    [Fact]
    public void ATickAfterAChordMatchedNothing_WritesTheLineWithTheRawModifiers()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            // Alt+Tab is not in the chord table: it passes through and is only remembered.
            Assert.False(harness.Platform.Raise(KeyboardKey.Tab, true, ModifierKeys.Alt));

            harness.Scheduler.Fire();

            var line = Assert.Single(harness.UnmatchedLines());
            Assert.StartsWith("unmatched chord: Alt+Tab raw=[", line, StringComparison.Ordinal);
            Assert.EndsWith("]", line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ATickWithNoNewFailure_DoesNotRepeatTheLine()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            harness.Platform.Raise(KeyboardKey.Tab, true, ModifierKeys.Alt);
            harness.Scheduler.Fire();

            harness.Scheduler.Fire();

            Assert.Single(harness.UnmatchedLines());
        }
    }

    /// <summary>The same failure twice changes the text (a repeat count), so the tick reports it again rather than hiding the second one.</summary>
    [Fact]
    public void ATickAfterTheSameChordFailedAgain_WritesTheRepeatCount()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            harness.Platform.Raise(KeyboardKey.Tab, true, ModifierKeys.Alt);
            harness.Scheduler.Fire();

            harness.Platform.Raise(KeyboardKey.Tab, true, ModifierKeys.Alt);
            harness.Scheduler.Fire();

            var lines = harness.UnmatchedLines();
            Assert.Equal(2, lines.Length);
            Assert.StartsWith("unmatched chord: Alt+Tab raw=[", lines[0], StringComparison.Ordinal);
            Assert.EndsWith("]", lines[0], StringComparison.Ordinal);
            Assert.Equal(lines[0] + " x2", lines[1]);
        }
    }

    [Fact]
    public void ATickBeforeAnyChordFailed_WritesNoLine()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            harness.Scheduler.Fire();

            Assert.Empty(harness.UnmatchedLines());
        }
    }
}
