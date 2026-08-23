using CosmicWin.App.Input;
using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.App.Tray;
using CosmicWin.Interop;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App.Tests;

/// <summary>
/// The redirect of <see cref="ArrivingWindowDesktopTests"/>, driven through the real composition.
/// </summary>
/// <remarks>
/// The rule itself is pinned at the adapter. What can only be wrong HERE is the answer to "which
/// desktop is the user on", because the composition is the only place that knows -- and the shell's
/// live answer is precisely the one that cannot be trusted at that moment.
/// <para>
/// The trap this exists to close: sampling the desktop only on the reconciliation tick leaves the
/// answer up to a full interval stale, so a window opened straight after <c>Alt+2</c> would be sent
/// BACK to desktop 1 -- the very defect, re-created by its own fix. The chord must refresh it.
/// </para>
/// </remarks>
public sealed class ArrivingWindowDesktopWiringTests
{
    /// <summary>Positional like the real service, and mutable so a test can simulate Windows moving the user on its own.</summary>
    private sealed class MutableVirtualDesktops : IVirtualDesktopService
    {
        private static readonly Guid[] Ids =
        [
            new("d1d1d1d1-0000-0000-0000-000000000001"),
            new("d2d2d2d2-0000-0000-0000-000000000002"),
            new("d3d3d3d3-0000-0000-0000-000000000003"),
        ];

        public bool IsSupported => true;

        public int Count => Ids.Length;

        public int CurrentIndex { get; set; } = 1;

        public string? LastError => null;

        public Guid CurrentDesktopId => IdOf(CurrentIndex);

        public List<(nint Handle, int Index)> Moved { get; } = [];

        public static Guid IdOf(int oneBasedIndex) => Ids[oneBasedIndex - 1];

        public bool TrySwitchTo(int oneBasedIndex)
        {
            CurrentIndex = oneBasedIndex;
            return true;
        }

        /// <summary>Deliberately does NOT follow the window -- the real one does not either.</summary>
        public bool TryMoveWindowTo(nint windowHandle, int oneBasedIndex)
        {
            Moved.Add((windowHandle, oneBasedIndex));
            return true;
        }
    }

    private sealed class NoForeground : IForegroundWindowSource
    {
        public nint GetForegroundHandle() => 0;
    }

    /// <summary>
    /// The only signal in this composition with a GUARANTEED happens-after relationship to the
    /// switch chord finishing its work.
    /// </summary>
    /// <remarks>
    /// Waiting on the service's own <c>CurrentIndex</c> instead made this test flaky -- one failure
    /// in three. That value becomes 2 INSIDE <c>TrySwitchTo</c>, while the refresh of "the desktop
    /// the user is on" happens afterwards in <c>DesktopSwitched</c>; the test could race past the
    /// gap and see the stale answer. <c>ActionExecutor.TryDispatchDesktop</c> records its trace line
    /// strictly after invoking <c>DesktopSwitched</c>, so this line appearing means that work is
    /// done.
    /// </remarks>
    private sealed class RecordingDesktopTrace : CosmicWin.App.Diagnostics.IDesktopTrace
    {
        private readonly List<string> _lines = [];

        public void Record(string line)
        {
            lock (_lines)
            {
                _lines.Add(line);
            }
        }

        public bool Recorded(string fragment)
        {
            lock (_lines)
            {
                return _lines.Any(line => line.Contains(fragment, StringComparison.Ordinal));
            }
        }
    }

    private sealed class NullTray : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class ImmediateScheduler
    {
        public Action? Callback { get; private set; }

        public IDisposable Schedule(TimeSpan interval, Action callback)
        {
            Callback = callback;
            return new NullTray();
        }

        public void Fire() => Callback!();
    }

    [Fact]
    public async Task AWindowBornOnAnotherDesktopRightAfterASwitchChord_IsBroughtToTheUser_AndTheUserStaysPut()
    {
        var workspace = new FakeWorkspace();
        var primary = new FakeDisplay(
            new IntPtr(1), Rectangle.FromSize(0, 0, 1920, 1080), Rectangle.FromSize(0, 0, 1920, 1080), 1.0, true);
        var registry = new WindowRegistry();
        var treeManager = new TreeManager([primary], primary, registry);
        var desktops = new MutableVirtualDesktops();
        var platform = new FakeKeyboardHookPlatform();
        var scheduler = new ImmediateScheduler();
        var trace = new RecordingDesktopTrace();
        var born = new RecordingWindow(new IntPtr(0x801), Rectangle.FromSize(0, 0, 800, 600));

        using var composition = AppComposition.Wire(
            workspace, treeManager, registry, new NoForeground(),
            new ExceptionListStore(ExceptionList.Empty),
            focusTrace: new RecordingFocusTrace(),
            disableTaskTrigger: () => { },
            scheduleReconcile: scheduler.Schedule,
            hookFactory: writer => new LowLevelKeyboardHook(writer, platform, TimeSpan.FromSeconds(5), () => 0),
            loadExceptions: () => ExceptionList.Empty,
            shutdown: () => { },
            buildTray: _ => new NullTray(),
            virtualDesktops: desktops,
            desktopTrace: trace,
            // The shell says the window was born on desktop 1, which is where the user is NOT.
            resolveWindowDesktop: _ => MutableVirtualDesktops.IdOf(1));

        // One pass with the user on desktop 1, so nothing about this is a cold start.
        scheduler.Fire();

        Assert.True(platform.Raise(KeyboardKey.D2, isKeyDown: true, ModifierKeys.Alt));
        Assert.True(await WaitUntil(() => trace.Recorded("SwitchDesktop arg=2"), TimeSpan.FromSeconds(5)));
        Assert.Equal(2, desktops.CurrentIndex);

        // Windows drags the view after the window it is about to create. No tick has run since the
        // chord, which is exactly the interval the naive fix left open.
        desktops.CurrentIndex = 1;
        workspace.RaiseWindowAdded(born);

        Assert.Equal([(born.Handle, 2)], desktops.Moved);
        Assert.Equal(2, desktops.CurrentIndex);
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        return condition();
    }
}
