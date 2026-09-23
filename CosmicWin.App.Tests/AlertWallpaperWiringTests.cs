using CosmicWin.App.Alerts;
using CosmicWin.App.Input;
using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.App.Tray;
using CosmicWin.Interop;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App.Tests;

/// <summary>
/// T8: named-pipe alert commands are accepted by AppComposition, queued, laid out, and handed to
/// the video wallpaper overlay on the ordinary 400 ms reconciliation tick.
/// </summary>
public sealed class AlertWallpaperWiringTests
{
    private sealed class NoForeground : IForegroundWindowSource
    {
        public nint GetForegroundHandle() => 0;
    }

    private sealed class NullDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class Scheduler
    {
        private Action? _callback;

        public IDisposable Schedule(TimeSpan interval, Action callback)
        {
            _callback = callback;
            return new NullDisposable();
        }

        public void Fire() => _callback!();
    }

    private sealed class RecordingDesktopTrace : CosmicWin.App.Diagnostics.IDesktopTrace
    {
        public List<string> Lines { get; } = [];

        public void Record(string line) => Lines.Add(line);
    }

    private sealed class FakeAlertCommandServer(Func<string, string> handleCommand) : IAlertCommandServer
    {
        public int StartCallCount { get; private set; }

        public int DisposeCallCount { get; private set; }

        public void Start() => StartCallCount++;

        public string Send(string command) => handleCommand(command);

        public void Dispose() => DisposeCallCount++;
    }

    private sealed class Counter
    {
        public int Value { get; private set; }

        public void Increment() => Value++;
    }

    private sealed record Harness(
        AppComposition Composition,
        Scheduler Scheduler,
        FakeAlertCommandServer? Server,
        List<IReadOnlyList<FrameOverlayTile>> TileSets,
        Counter ClearCount,
        RecordingDesktopTrace Trace);

    private static Harness Wire(bool alertsEnabled = true)
    {
        var workspace = new FakeWorkspace();
        var primary = new FakeDisplay(
            new IntPtr(1), Rectangle.FromSize(0, 0, 1920, 1080), Rectangle.FromSize(0, 0, 1920, 1080), 1.0, true);
        var registry = new WindowRegistry();
        var treeManager = new TreeManager([primary], primary, registry);
        var scheduler = new Scheduler();
        var trace = new RecordingDesktopTrace();
        var tileSets = new List<IReadOnlyList<FrameOverlayTile>>();
        FakeAlertCommandServer? server = null;
        var clearCount = new Counter();

        var composition = AppComposition.Wire(
            workspace, treeManager, registry, new NoForeground(), new ExceptionListStore(ExceptionList.Empty),
            focusTrace: new RecordingFocusTrace(),
            disableTaskTrigger: () => { },
            scheduleReconcile: scheduler.Schedule,
            hookFactory: writer => new LowLevelKeyboardHook(
                writer, new FakeKeyboardHookPlatform(), TimeSpan.FromSeconds(5), () => 0),
            loadExceptions: () => ExceptionList.Empty,
            shutdown: () => { },
            buildTray: _ => new NullDisposable(),
            importVideoWallpaper: path => path,
            desktopTrace: trace,
            alertsEnabled: alertsEnabled,
            createAlertCommandServer: (_, handle, _) => server = new FakeAlertCommandServer(handle),
            setAlertOverlayTiles: tiles => tileSets.Add(tiles.ToArray()),
            clearAlertOverlay: clearCount.Increment,
            alertDesktopVisible: () => true);

        return new Harness(composition, scheduler, server, tileSets, clearCount, trace);
    }

    [Fact]
    public void AlertsDisabled_DoesNotStartServerOrUpdateOverlay()
    {
        var harness = Wire(alertsEnabled: false);
        using (harness.Composition)
        {
            Assert.Null(harness.Server);

            harness.Scheduler.Fire();

            Assert.Empty(harness.TileSets);
            Assert.Equal(0, harness.ClearCount.Value);
        }
    }

    [Fact]
    public void AlertsEnabled_StartsServer()
    {
        var harness = Wire(alertsEnabled: true);
        using (harness.Composition)
        {
            Assert.NotNull(harness.Server);
            Assert.Equal(1, harness.Server!.StartCallCount);
        }
    }

    [Fact]
    public void ValidAlertCommand_IsAcceptedAndUpdatesOverlayOnTickInCommandOrder()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            var reply = harness.Server!.Send("warning:2 failed:1 duration:5");

            Assert.Equal(AlertPipeProtocol.OkReply, reply);

            harness.Scheduler.Fire();

            var tiles = Assert.Single(harness.TileSets);
            Assert.Equal(3, tiles.Count);
            Assert.Collection(
                tiles,
                tile => Assert.Equal(FrameOverlayTileKind.Warning, tile.Kind),
                tile => Assert.Equal(FrameOverlayTileKind.Warning, tile.Kind),
                tile => Assert.Equal(FrameOverlayTileKind.Failed, tile.Kind));
            Assert.All(tiles, tile =>
            {
                Assert.NotEqual(default, tile.StartedAt);
                Assert.Equal(TimeSpan.FromSeconds(5), tile.Duration);
                Assert.True(tile.Bounds.Width > 0);
                Assert.True(tile.Bounds.Height > 0);
            });
        }
    }

    [Fact]
    public void MalformedAlertCommand_ReturnsErrorAndDoesNotUpdateOverlay()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            var reply = harness.Server!.Send("nonsense");

            Assert.StartsWith("error: ", reply, StringComparison.Ordinal);

            harness.Scheduler.Fire();

            Assert.Empty(harness.TileSets);
            Assert.Equal(1, harness.ClearCount.Value);
            Assert.Contains(harness.Trace.Lines, line => line.Contains("alert rejected", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ExpiredAlert_ClearsOverlayOnLaterTick()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            Assert.Equal(AlertPipeProtocol.OkReply, harness.Server!.Send("warning:1 duration:1"));
            harness.Scheduler.Fire();
            Assert.NotEmpty(harness.TileSets);

            Thread.Sleep(TimeSpan.FromSeconds(1.1));
            harness.Scheduler.Fire();

            Assert.Equal(1, harness.ClearCount.Value);
        }
    }
}
