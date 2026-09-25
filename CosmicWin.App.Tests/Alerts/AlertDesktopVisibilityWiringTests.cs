using CosmicWin.App.Alerts;
using CosmicWin.App.Input;
using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.App.Tray;
using CosmicWin.Interop;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;
using Windows.Win32.Graphics.Direct3D11;

namespace CosmicWin.App.Tests.Alerts;

/// <summary>
/// T10 (live-alert-wallpaper): T9 found production wiring an alert's visibility to
/// <c>videoWallpaperActive</c> alone, which stays true while a fullscreen window covers the primary
/// monitor -- so an alert played out unseen instead of being held. This proves the FIX at the
/// composition level, through the real <c>isPrimaryMonitorCovered</c> seam (not <c>
/// alertDesktopVisible</c>, which bypasses this composition entirely and is what every earlier
/// alert-wiring test uses): a covered desktop holds a queued alert, and it is shown once the fake
/// coverage source reports uncovered.
/// </summary>
public sealed class AlertDesktopVisibilityWiringTests
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

    private sealed class FakeAlertCommandServer(Func<string, string> handleCommand) : IAlertCommandServer
    {
        public void Start()
        {
        }

        public string Send(string command) => handleCommand(command);

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Local, not shared: <see cref="IVideoWallpaperHost"/>'s <c>Device</c>/<c>GetBackBuffer</c> are
    /// `internal` interface members satisfiable only within <c>CosmicWin.Interop</c> or its
    /// InternalsVisibleTo friends -- mirrors <c>VideoWallpaperPlaybackWiringTests</c>'s own local
    /// fake rather than reusing one, matching that file's own remarks on why.
    /// </summary>
    private sealed class FakeVideoWallpaperHost : IVideoWallpaperHost
    {
        public bool TryAttach() => true;

        ID3D11Device IVideoWallpaperHost.Device =>
            throw new InvalidOperationException("This wiring test never reads a real D3D11 device.");

        ID3D11Texture2D IVideoWallpaperHost.GetBackBuffer() =>
            throw new InvalidOperationException("This wiring test never reads a real back buffer.");

        public void Present()
        {
        }

        // T4 (webview-alert-layer): plain ints/floats, unlike Device/GetBackBuffer above -- this
        // wiring test never triggers a shake, so the defaults are never read.
        public (int Width, int Height) BackBufferSize => (0, 0);

        public void SetVideoTransform(
            float centerX, float centerY, float offsetX, float offsetY, float angleDegrees, float scale)
        {
        }

        public void ClearVideoTransform()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeVideoWallpaperPlayer : IVideoWallpaperPlayer
    {
        public bool TryPlay(IVideoWallpaperHost host, string videoPath) => true;

        public void Stop()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed record Harness(
        AppComposition Composition, Scheduler Scheduler, FakeAlertCommandServer Server,
        List<IReadOnlyList<FrameOverlayTile>> TileSets);

    private static Harness Wire(Func<bool> isPrimaryMonitorCovered)
    {
        var workspace = new FakeWorkspace();
        var primary = new FakeDisplay(
            new IntPtr(1), Rectangle.FromSize(0, 0, 1920, 1080), Rectangle.FromSize(0, 0, 1920, 1080), 1.0, true);
        var registry = new WindowRegistry();
        var treeManager = new TreeManager([primary], primary, registry);
        var scheduler = new Scheduler();
        var tileSets = new List<IReadOnlyList<FrameOverlayTile>>();
        FakeAlertCommandServer? server = null;
        var path = typeof(AlertDesktopVisibilityWiringTests).Assembly.Location;

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
            importVideoWallpaper: p => p,
            alertsEnabled: true,
            createAlertCommandServer: (_, handle, _) => server = new FakeAlertCommandServer(handle),
            setAlertOverlayTiles: tiles => tileSets.Add(tiles.ToArray()),
            clearAlertOverlay: () => { },
            // Deliberately NOT set: leaving this null is what lets the production fallback --
            // videoWallpaperActive.Value && !isPrimaryMonitorCovered() -- actually run.
            alertDesktopVisible: null,
            isPrimaryMonitorCovered: isPrimaryMonitorCovered,
            videoWallpaperHost: new FakeVideoWallpaperHost(),
            videoWallpaperPlayer: new FakeVideoWallpaperPlayer(),
            videoWallpaperPath: path,
            scheduleVideoWallpaperWork: work => work());

        return new Harness(composition, scheduler, server!, tileSets);
    }

    [Fact]
    public void ACoveredDesktop_HoldsTheAlertInsteadOfShowingIt()
    {
        var harness = Wire(isPrimaryMonitorCovered: () => true);
        using (harness.Composition)
        {
            Assert.Equal(AlertPipeProtocol.OkReply, harness.Server.Send("warning:1"));

            harness.Scheduler.Fire();

            Assert.Empty(harness.TileSets);
        }
    }

    [Fact]
    public void AnUncoveredDesktop_ShowsAQueuedAlert()
    {
        var harness = Wire(isPrimaryMonitorCovered: () => false);
        using (harness.Composition)
        {
            Assert.Equal(AlertPipeProtocol.OkReply, harness.Server.Send("warning:1"));

            // T9d: enqueuing itself already ticks the overlay once, so the watch tick below repeats
            // the SAME (idempotent) tile set rather than being the only update.
            Assert.NotEmpty(harness.TileSets);

            harness.Scheduler.Fire();

            Assert.Single(harness.TileSets[^1]);
        }
    }

    [Fact]
    public void AnAlertQueuedWhileCovered_IsShownOnceUncovered()
    {
        var covered = true;
        var harness = Wire(isPrimaryMonitorCovered: () => covered);
        using (harness.Composition)
        {
            Assert.Equal(AlertPipeProtocol.OkReply, harness.Server.Send("warning:1"));

            harness.Scheduler.Fire();
            Assert.Empty(harness.TileSets);

            covered = false;
            harness.Scheduler.Fire();

            var tiles = Assert.Single(harness.TileSets);
            Assert.Single(tiles);
        }
    }
}
