using System.IO;
using CosmicWin.App.Input;
using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.App.Tray;
using CosmicWin.Interop;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;
using Windows.Win32.Graphics.Direct3D11;

namespace CosmicWin.App.Tests;

/// <summary>
/// T6: the T3/T4 host+player are constructed and attached/played from
/// <see cref="AppComposition"/> itself. <see cref="VideoWallpaperWiringTests"/> covers T5's own
/// scope (the tray hands the raw path to the import delegate and persists the RETURN value); this
/// file covers what happens once the (re)start-playback hook -- deliberately left a no-op there --
/// is wired up: startup activation when a path is already configured, the tray pick triggering
/// (re)start with the IMPORTED path, and disposal ordering.
/// </summary>
public sealed class VideoWallpaperPlaybackWiringTests
{
    private sealed class NoForeground : IForegroundWindowSource
    {
        public nint Handle { get; set; }

        public nint GetForegroundHandle() => Handle;
    }

    private sealed class NullDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Captures the 400ms watch tick's own callback so a test can fire it on demand -- the same
    /// shape as <c>WorkAreaTrackingTests.Scheduler</c>, declared locally here for the reason that
    /// file's own remarks give for its local fakes: no shared test-double project reaches both.
    /// </summary>
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

    /// <summary>
    /// In-memory <see cref="IVideoWallpaperHost"/>. Mirrors
    /// <c>CosmicWin.Interop.Tests.Win32.FakeVideoWallpaperHost</c>'s call-recording shape, but is
    /// declared locally rather than reused from there: that fake is <c>internal</c> to
    /// <c>CosmicWin.Interop.Tests</c>, which grants no <c>InternalsVisibleTo</c> back to this
    /// project, the same reason <see cref="FloatingDialogTests"/> declares its own local
    /// <c>FakeWindowShownWatcher</c> rather than reusing one from elsewhere.
    /// </summary>
    private sealed class FakeVideoWallpaperHost(List<string>? events = null) : IVideoWallpaperHost
    {
        public int TryAttachCallCount { get; private set; }

        public bool TryAttachReturns { get; set; } = true;

        public int DisposeCallCount { get; private set; }

        public bool TryAttach()
        {
            TryAttachCallCount++;
            events?.Add("host.TryAttach");
            return TryAttachReturns;
        }

        // Explicit implementation: IVideoWallpaperHost.Device/GetBackBuffer are `internal`
        // interface members (CsWin32's D3D types are internal to CosmicWin.Interop), and none of
        // these wiring tests exercise real D3D -- matches the throwing shape of the real internal
        // fake this one stands in for.
        ID3D11Device IVideoWallpaperHost.Device =>
            throw new InvalidOperationException(
                "FakeVideoWallpaperHost never creates a real D3D11 device -- these composition " +
                "wiring tests never read it.");

        ID3D11Texture2D IVideoWallpaperHost.GetBackBuffer() =>
            throw new InvalidOperationException(
                "FakeVideoWallpaperHost never creates a real back buffer -- these composition " +
                "wiring tests never read it.");

        public void Present()
        {
        }

        // T4 (webview-alert-layer): plain ints/floats, unlike Device/GetBackBuffer above -- these
        // composition wiring tests never trigger a shake, so the defaults are never read.
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
            DisposeCallCount++;
            events?.Add("host.Dispose");
        }
    }

    private sealed class FakeVideoWallpaperPlayer(List<string>? events = null) : IVideoWallpaperPlayer
    {
        public int TryPlayCallCount { get; private set; }

        public bool TryPlayReturns { get; set; } = true;

        public IVideoWallpaperHost? LastHost { get; private set; }

        public string? LastVideoPath { get; private set; }

        public int DisposeCallCount { get; private set; }

        public int StopCallCount { get; private set; }

        public bool TryPlay(IVideoWallpaperHost host, string videoPath)
        {
            TryPlayCallCount++;
            LastHost = host;
            LastVideoPath = videoPath;
            events?.Add("player.TryPlay");
            return TryPlayReturns;
        }

        public void Stop()
        {
            StopCallCount++;
            events?.Add("player.Stop");
        }

        public void Dispose()
        {
            DisposeCallCount++;
            events?.Add("player.Dispose");
        }
    }

    /// <summary>No-op stand-in for the shared HTTP server -- only ever constructed here to capture
    /// the delegate <c>AppComposition.Wire</c> hands it, never actually listens.</summary>
    private sealed class FakeHttpServer : IAlertCommandServer
    {
        public void Start()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed record Harness(
        AppComposition Composition, TrayMenuController Tray, Func<string, bool>? HandleVideoWallpaperHttpSwitch,
        Func<string, bool>? HandleWallpaperSceneHttpSwitch);

    private static Harness Wire(
        IVideoWallpaperHost? videoWallpaperHost = null,
        IVideoWallpaperPlayer? videoWallpaperPlayer = null,
        string? videoWallpaperPath = null,
        Func<string, string>? importVideoWallpaper = null,
        CosmicWin.App.Diagnostics.IDesktopTrace? desktopTrace = null,
        Action<Action>? scheduleVideoWallpaperWork = null,
        Action? disposeVideoWallpaper = null,
        Action<string>? persistVideoWallpaperPath = null,
        Func<TimeSpan, Action, IDisposable>? scheduleReconcile = null,
        bool videoWallpaperHttpEnabled = false,
        Func<string, VideoWallpaperImport.VideoFileSnapshot?>? readVideoFileSnapshot = null,
        WallpaperMode wallpaperMode = WallpaperMode.Video,
        // S4 (wallpaper-scene-http-endpoint).
        bool wallpaperSceneHttpEnabled = false,
        Func<WallpaperScene, bool>? switchHtmlWallpaperScene = null,
        Action<WallpaperScene>? persistWallpaperScene = null,
        Action<Action>? scheduleOnOwningThread = null)
    {
        var workspace = new FakeWorkspace();
        var primary = new FakeDisplay(
            new IntPtr(1), Rectangle.FromSize(0, 0, 1920, 1080), Rectangle.FromSize(0, 0, 1920, 1080), 1.0, true);
        var registry = new WindowRegistry();
        var treeManager = new TreeManager([primary], primary, registry);
        var foreground = new NoForeground();
        TrayMenuController? tray = null;
        Func<string, bool>? capturedVideoSwitchHandler = null;
        Func<string, bool>? capturedSceneSwitchHandler = null;

        var composition = AppComposition.Wire(
            workspace, treeManager, registry, foreground, new ExceptionListStore(ExceptionList.Empty),
            focusTrace: new RecordingFocusTrace(),
            disableTaskTrigger: () => { },
            scheduleReconcile: scheduleReconcile ?? ((_, _) => new NullDisposable()),
            hookFactory: writer => new LowLevelKeyboardHook(
                writer, new FakeKeyboardHookPlatform(), TimeSpan.FromSeconds(5), () => 0),
            loadExceptions: () => ExceptionList.Empty,
            shutdown: () => { },
            buildTray: controller =>
            {
                tray = controller;
                return new NullDisposable();
            },
            importVideoWallpaper: importVideoWallpaper ?? (path => path),
            desktopTrace: desktopTrace,
            scheduleOnOwningThread: scheduleOnOwningThread,
            videoWallpaperHost: videoWallpaperHost,
            videoWallpaperPlayer: videoWallpaperPlayer,
            scheduleVideoWallpaperWork: scheduleVideoWallpaperWork,
            disposeVideoWallpaper: disposeVideoWallpaper,
            videoWallpaperPath: videoWallpaperPath,
            persistVideoWallpaperPath: persistVideoWallpaperPath,
            videoWallpaperHttpEnabled: videoWallpaperHttpEnabled,
            wallpaperMode: wallpaperMode,
            readVideoFileSnapshot: readVideoFileSnapshot,
            wallpaperSceneHttpEnabled: wallpaperSceneHttpEnabled,
            switchHtmlWallpaperScene: switchHtmlWallpaperScene,
            persistWallpaperScene: persistWallpaperScene,
            loadAlertHttpToken: () => "test-token",
            createLocalHttpCommandServer: (_, _, _, _, videoSwitch, sceneSwitch) =>
            {
                capturedVideoSwitchHandler = videoSwitch;
                capturedSceneSwitchHandler = sceneSwitch;
                return new FakeHttpServer();
            });

        return new Harness(composition, tray!, capturedVideoSwitchHandler, capturedSceneSwitchHandler);
    }

    [Fact]
    public void Startup_WithConfiguredPathAndBothCollaborators_AttachesThenPlaysWithTheConfiguredPath()
    {
        var events = new List<string>();
        var host = new FakeVideoWallpaperHost(events);
        var player = new FakeVideoWallpaperPlayer(events);
        var trace = new RecordingDesktopTrace();
        var path = typeof(VideoWallpaperPlaybackWiringTests).Assembly.Location;

        var harness = Wire(
            videoWallpaperHost: host, videoWallpaperPlayer: player, videoWallpaperPath: path,
            desktopTrace: trace);
        using (harness.Composition)
        {
            Assert.Equal(1, host.TryAttachCallCount);
            Assert.Equal(1, player.TryPlayCallCount);
            Assert.Same(host, player.LastHost);
            Assert.Equal(path, player.LastVideoPath);
            Assert.Equal(["host.TryAttach", "player.TryPlay"], events);
            Assert.Equal(
                ["video-wallpaper phase=startup pathExists=True tryAttach=True tryPlay=True"],
                trace.Lines);
        }
    }

    [Fact]
    public void Startup_WithNoConfiguredPath_NeitherAttachesNorPlays()
    {
        var host = new FakeVideoWallpaperHost();
        var player = new FakeVideoWallpaperPlayer();

        var harness = Wire(videoWallpaperHost: host, videoWallpaperPlayer: player, videoWallpaperPath: null);
        using (harness.Composition)
        {
            Assert.Equal(0, host.TryAttachCallCount);
            Assert.Equal(0, player.TryPlayCallCount);
        }
    }

    /// <summary>
    /// D3 (html-wallpaper-demo, demo/html-wallpaper-d3-switch): in html mode startup attaches the
    /// SAME host with no player involved at all -- never TryPlay -- even though a video path is
    /// configured (decision: html mode always wins over a stale video-path setting). Traced with
    /// mode=html so it reads distinctly from an ordinary video startup line.
    /// </summary>
    [Fact]
    public void Startup_InHtmlMode_AttachesTheHostWithoutPlayingEvenWithAConfiguredPath()
    {
        var events = new List<string>();
        var queued = new Queue<Action>();
        var host = new FakeVideoWallpaperHost(events);
        var player = new FakeVideoWallpaperPlayer(events);
        var trace = new RecordingDesktopTrace();
        var path = typeof(VideoWallpaperPlaybackWiringTests).Assembly.Location;

        var harness = Wire(
            videoWallpaperHost: host, videoWallpaperPlayer: player, videoWallpaperPath: path,
            desktopTrace: trace, scheduleVideoWallpaperWork: queued.Enqueue,
            wallpaperMode: WallpaperMode.Html);
        using (harness.Composition)
        {
            Assert.Single(queued);
            queued.Dequeue().Invoke();

            Assert.Equal(1, host.TryAttachCallCount);
            Assert.Equal(0, player.TryPlayCallCount);
            Assert.Equal(["host.TryAttach"], events);
            Assert.Equal(
                ["video-wallpaper phase=startup mode=html attached=True"],
                trace.Lines);
        }
    }

    /// <summary>D1: html mode needs no configured path at all -- there is nothing to play, only a host to attach.</summary>
    [Fact]
    public void Startup_InHtmlMode_WithNoConfiguredPath_StillAttachesTheHost()
    {
        var queued = new Queue<Action>();
        var host = new FakeVideoWallpaperHost();
        var player = new FakeVideoWallpaperPlayer();

        var harness = Wire(
            videoWallpaperHost: host, videoWallpaperPlayer: player, videoWallpaperPath: null,
            scheduleVideoWallpaperWork: queued.Enqueue, wallpaperMode: WallpaperMode.Html);
        using (harness.Composition)
        {
            Assert.Single(queued);
            queued.Dequeue().Invoke();

            Assert.Equal(1, host.TryAttachCallCount);
            Assert.Equal(0, player.TryPlayCallCount);
        }
    }

    /// <summary>Unwired collaborators (the default in every test that predates T6) must never make startup throw.</summary>
    [Fact]
    public void Startup_WithNoCollaboratorsWired_DoesNotThrow()
    {
        var harness = Wire();

        var exception = Record.Exception(() => harness.Composition.Dispose());

        Assert.Null(exception);
    }

    [Fact]
    public void Startup_UsesTheVideoWallpaperSchedulerInsteadOfRunningOnTheCallerThread()
    {
        var queued = new Queue<Action>();
        var host = new FakeVideoWallpaperHost();
        var player = new FakeVideoWallpaperPlayer();
        var path = typeof(VideoWallpaperPlaybackWiringTests).Assembly.Location;

        var harness = Wire(
            videoWallpaperHost: host, videoWallpaperPlayer: player, videoWallpaperPath: path,
            scheduleVideoWallpaperWork: queued.Enqueue);
        using (harness.Composition)
        {
            Assert.Equal(0, host.TryAttachCallCount);
            Assert.Equal(0, player.TryPlayCallCount);

            queued.Dequeue().Invoke();

            Assert.Equal(1, host.TryAttachCallCount);
            Assert.Equal(1, player.TryPlayCallCount);
        }
    }

    [Fact]
    public void Disposing_UsesTheSuppliedVideoWallpaperDisposer()
    {
        var host = new FakeVideoWallpaperHost();
        var player = new FakeVideoWallpaperPlayer();
        var disposed = false;

        var harness = Wire(
            videoWallpaperHost: host, videoWallpaperPlayer: player,
            disposeVideoWallpaper: () => disposed = true);

        harness.Composition.Dispose();

        Assert.True(disposed);
        Assert.Equal(0, player.DisposeCallCount);
        Assert.Equal(0, host.DisposeCallCount);
    }

    [Fact]
    public void PickingAVideo_AttachesAndPlaysWithTheImportedPath()
    {
        var host = new FakeVideoWallpaperHost();
        var player = new FakeVideoWallpaperPlayer();
        const string imported = @"C:\LOCALAPPDATA\CosmicWin\video-wallpaper.mp4";

        var harness = Wire(
            videoWallpaperHost: host, videoWallpaperPlayer: player,
            importVideoWallpaper: _ => imported);
        using (harness.Composition)
        {
            harness.Tray.SetVideoWallpaperPath(@"C:\Users\me\Videos\clip.mp4");

            Assert.Equal(1, host.TryAttachCallCount);
            Assert.Equal(1, player.TryPlayCallCount);
            Assert.Same(host, player.LastHost);
            Assert.Equal(imported, player.LastVideoPath);
        }
    }

    [Fact]
    public void Startup_WhenAttachFails_RecordsPathAndSkippedPlayResult()
    {
        var host = new FakeVideoWallpaperHost { TryAttachReturns = false };
        var player = new FakeVideoWallpaperPlayer();
        var trace = new RecordingDesktopTrace();
        const string missingPath = @"C:\LOCALAPPDATA\CosmicWin\missing-video-wallpaper.mp4";

        var harness = Wire(
            videoWallpaperHost: host, videoWallpaperPlayer: player, videoWallpaperPath: missingPath,
            desktopTrace: trace);
        using (harness.Composition)
        {
            Assert.Equal(1, host.TryAttachCallCount);
            Assert.Equal(0, player.TryPlayCallCount);
            Assert.Equal(
                ["video-wallpaper phase=startup pathExists=False tryAttach=False tryPlay=skipped"],
                trace.Lines);
        }
    }

    [Fact]
    public void PickingAVideo_RecordsImportedPathAttachAndPlayResults()
    {
        var host = new FakeVideoWallpaperHost();
        var player = new FakeVideoWallpaperPlayer { TryPlayReturns = false };
        var trace = new RecordingDesktopTrace();
        var imported = typeof(VideoWallpaperPlaybackWiringTests).Assembly.Location;

        var harness = Wire(
            videoWallpaperHost: host, videoWallpaperPlayer: player,
            importVideoWallpaper: _ => imported,
            desktopTrace: trace);
        using (harness.Composition)
        {
            harness.Tray.SetVideoWallpaperPath(@"C:\Users\me\Videos\clip.mp4");

            Assert.Equal(1, host.TryAttachCallCount);
            Assert.Equal(1, player.TryPlayCallCount);
            Assert.Equal(
                ["video-wallpaper phase=pick pathExists=True tryAttach=True tryPlay=False"],
                trace.Lines);
        }
    }

    /// <summary>
    /// Both the FIRST-ever pick (no path was configured at startup, so nothing attached/played yet)
    /// and a later RE-pick must reach TryAttach/TryPlay again -- TryAttach is documented idempotent
    /// and TryPlay is documented to restart cleanly, so the closure calls both every time rather
    /// than branching on whether this is the first pick.
    /// </summary>
    [Fact]
    public void PickingAVideoTwice_ReattachesAndReplaysWithEachImportedPath()
    {
        var host = new FakeVideoWallpaperHost();
        var player = new FakeVideoWallpaperPlayer();
        var imports = new Queue<string>(
        [
            @"C:\LOCALAPPDATA\CosmicWin\video-wallpaper.mp4",
            @"C:\LOCALAPPDATA\CosmicWin\video-wallpaper.mp4",
        ]);

        var harness = Wire(
            videoWallpaperHost: host, videoWallpaperPlayer: player,
            importVideoWallpaper: _ => imports.Dequeue());
        using (harness.Composition)
        {
            harness.Tray.SetVideoWallpaperPath(@"C:\Users\me\Videos\first.mp4");
            harness.Tray.SetVideoWallpaperPath(@"C:\Users\me\Videos\second.mp4");

            Assert.Equal(2, host.TryAttachCallCount);
            Assert.Equal(2, player.TryPlayCallCount);
        }
    }

    /// <summary>
    /// The player's worker thread reads the host's D3D11 device/swapchain on every tick, so it must
    /// be stopped before the host is torn down.
    /// </summary>
    [Fact]
    public void Disposing_DisposesThePlayerBeforeTheHost()
    {
        var events = new List<string>();
        var host = new FakeVideoWallpaperHost(events);
        var player = new FakeVideoWallpaperPlayer(events);

        var harness = Wire(videoWallpaperHost: host, videoWallpaperPlayer: player);
        harness.Composition.Dispose();

        Assert.Equal(["player.Dispose", "host.Dispose"], events);
    }

    /// <summary>
    /// T1 (video-wallpaper-repick-and-slideshow): re-picking a video while one is already playing
    /// used to import onto the fixed destination BEFORE stopping playback, and Media Foundation
    /// still held that file open -- a sharing violation, and the new video never played. The fix
    /// stops first, so the whole sequence must run in this order: player.Stop, then the import,
    /// then the (re)attach and (re)play.
    /// </summary>
    [Fact]
    public void PickingAVideo_StopsPlaybackBeforeImportingBeforeReattachingAndReplaying()
    {
        var events = new List<string>();
        var host = new FakeVideoWallpaperHost(events);
        var player = new FakeVideoWallpaperPlayer(events);
        const string imported = @"C:\LOCALAPPDATA\CosmicWin\video-wallpaper.mp4";

        var harness = Wire(
            videoWallpaperHost: host, videoWallpaperPlayer: player,
            importVideoWallpaper: _ =>
            {
                events.Add("import");
                return imported;
            });
        using (harness.Composition)
        {
            harness.Tray.SetVideoWallpaperPath(@"C:\Users\me\Videos\clip.mp4");

            Assert.Equal(["player.Stop", "import", "host.TryAttach", "player.TryPlay"], events);
        }
    }

    /// <summary>
    /// Constraint from the feature doc: "picking a video must never leave the wallpaper dead". When
    /// the import throws (the scenario bug 1 describes, or any other import failure), the failure is
    /// traced with no absolute paths, the PREVIOUS video is re-played so playback survives, and
    /// nothing is persisted -- the failed pick never landed on disk under the fixed destination.
    /// </summary>
    [Fact]
    public void PickingAVideo_WhenImportThrows_TracesAndRestoresThePreviousVideoWithoutPersisting()
    {
        var host = new FakeVideoWallpaperHost();
        var player = new FakeVideoWallpaperPlayer();
        var trace = new RecordingDesktopTrace();
        var persisted = new List<string>();
        var previousPath = typeof(VideoWallpaperPlaybackWiringTests).Assembly.Location;

        var harness = Wire(
            videoWallpaperHost: host, videoWallpaperPlayer: player, videoWallpaperPath: previousPath,
            desktopTrace: trace, persistVideoWallpaperPath: persisted.Add,
            importVideoWallpaper: _ => throw new IOException("sharing violation"));
        using (harness.Composition)
        {
            // Startup already activated the previously-configured video and traced a
            // phase=startup line -- cleared so the assertions below read only the pick itself.
            trace.Lines.Clear();

            var exception = Record.Exception(
                () => harness.Tray.SetVideoWallpaperPath(@"C:\Users\me\Videos\clip.mp4"));

            Assert.Null(exception);
            Assert.Contains("video-wallpaper phase=pick import-failed error=IOException", trace.Lines);
            Assert.Contains(
                trace.Lines,
                line => line.StartsWith("video-wallpaper phase=restore") && line.Contains("tryPlay=True"));
            Assert.Empty(persisted);
        }
    }

    /// <summary>The other half of the restore contract: with no previous video configured, there is
    /// nothing to fall back to, so nothing is played -- but the failure still must not throw out of
    /// the tray call.</summary>
    [Fact]
    public void PickingAVideo_WhenImportThrowsWithNoPreviousPath_PlaysNothingAndDoesNotThrow()
    {
        var host = new FakeVideoWallpaperHost();
        var player = new FakeVideoWallpaperPlayer();
        var trace = new RecordingDesktopTrace();

        var harness = Wire(
            videoWallpaperHost: host, videoWallpaperPlayer: player, desktopTrace: trace,
            importVideoWallpaper: _ => throw new IOException("sharing violation"));
        using (harness.Composition)
        {
            var exception = Record.Exception(
                () => harness.Tray.SetVideoWallpaperPath(@"C:\Users\me\Videos\clip.mp4"));

            Assert.Null(exception);
            Assert.Equal(0, host.TryAttachCallCount);
            Assert.Equal(0, player.TryPlayCallCount);
            Assert.Contains("video-wallpaper phase=pick import-failed error=IOException", trace.Lines);
        }
    }

    /// <summary>
    /// Two picks queued before the video thread runs either: the second one's fallback must be the
    /// video the FIRST one landed, not whatever was configured when the second was clicked. On a
    /// machine's first-ever pick that earlier value is null, and a fallback read too early leaves
    /// the wallpaper dead even though the first pick succeeded.
    /// </summary>
    [Fact]
    public void TwoQueuedPicks_WhenTheSecondImportThrows_RestoresTheVideoTheFirstPickLanded()
    {
        var queued = new Queue<Action>();
        var host = new FakeVideoWallpaperHost();
        var player = new FakeVideoWallpaperPlayer();
        var trace = new RecordingDesktopTrace();
        var landed = typeof(VideoWallpaperPlaybackWiringTests).Assembly.Location;
        var imports = new Queue<Func<string>>(
            [() => landed, () => throw new IOException("sharing violation")]);

        var harness = Wire(
            videoWallpaperHost: host, videoWallpaperPlayer: player, desktopTrace: trace,
            scheduleVideoWallpaperWork: queued.Enqueue,
            importVideoWallpaper: _ => imports.Dequeue()());
        using (harness.Composition)
        {
            harness.Tray.SetVideoWallpaperPath(@"C:\Users\me\Videos\first.mp4");
            harness.Tray.SetVideoWallpaperPath(@"C:\Users\me\Videos\second.mp4");
            while (queued.Count > 0)
            {
                queued.Dequeue().Invoke();
            }

            Assert.Contains(
                trace.Lines,
                line => line.StartsWith("video-wallpaper phase=restore") && line.Contains("tryPlay=True"));
            Assert.Equal(landed, player.LastVideoPath);
        }
    }

    /// <summary>
    /// T3 (video-wallpaper-repick-and-slideshow): T2 proved live that the Windows wallpaper
    /// slideshow inserts a fresh WorkerW directly above the host every time it changes image, and
    /// nothing ever re-raised it. The existing 400ms watch tick is what now notices -- but only
    /// while a video is genuinely playing, so a tick on a machine that never configured one, or
    /// whose last activation failed, costs nothing.
    /// </summary>
    [Fact]
    public void ReconcileTick_WithAnActiveVideoWallpaper_PostsExactlyOneKeepAliveTryAttach()
    {
        var scheduler = new Scheduler();
        var queued = new Queue<Action>();
        var host = new FakeVideoWallpaperHost();
        var player = new FakeVideoWallpaperPlayer();
        var path = typeof(VideoWallpaperPlaybackWiringTests).Assembly.Location;

        var harness = Wire(
            videoWallpaperHost: host, videoWallpaperPlayer: player, videoWallpaperPath: path,
            scheduleVideoWallpaperWork: queued.Enqueue, scheduleReconcile: scheduler.Schedule);
        using (harness.Composition)
        {
            // Drains the startup activation's own posted work item -- it is what makes the video
            // ACTIVE in the first place, and must not be mistaken for a keep-alive post below.
            Assert.Single(queued);
            queued.Dequeue().Invoke();
            Assert.Equal(1, host.TryAttachCallCount);

            scheduler.Fire();

            Assert.Single(queued);
            queued.Dequeue().Invoke();
            Assert.Equal(2, host.TryAttachCallCount);
        }
    }

    [Fact]
    public void ReconcileTick_WithNoConfiguredVideo_PostsNothing()
    {
        var scheduler = new Scheduler();
        var queued = new Queue<Action>();
        var host = new FakeVideoWallpaperHost();
        var player = new FakeVideoWallpaperPlayer();

        var harness = Wire(
            videoWallpaperHost: host, videoWallpaperPlayer: player, videoWallpaperPath: null,
            scheduleVideoWallpaperWork: queued.Enqueue, scheduleReconcile: scheduler.Schedule);
        using (harness.Composition)
        {
            Assert.Empty(queued);

            scheduler.Fire();

            Assert.Empty(queued);
            Assert.Equal(0, host.TryAttachCallCount);
        }
    }

    [Fact]
    public void ReconcileTick_WhenStartupTryPlayFailed_PostsNothing()
    {
        var scheduler = new Scheduler();
        var queued = new Queue<Action>();
        var host = new FakeVideoWallpaperHost();
        var player = new FakeVideoWallpaperPlayer { TryPlayReturns = false };
        var path = typeof(VideoWallpaperPlaybackWiringTests).Assembly.Location;

        var harness = Wire(
            videoWallpaperHost: host, videoWallpaperPlayer: player, videoWallpaperPath: path,
            scheduleVideoWallpaperWork: queued.Enqueue, scheduleReconcile: scheduler.Schedule);
        using (harness.Composition)
        {
            // Drains the startup activation: it attaches but fails to play, so the wallpaper is
            // NOT active and the tick below must post nothing.
            queued.Dequeue().Invoke();
            Assert.Equal(1, host.TryAttachCallCount);
            Assert.Equal(1, player.TryPlayCallCount);

            scheduler.Fire();

            Assert.Empty(queued);
            Assert.Equal(1, host.TryAttachCallCount);
        }
    }

    /// <summary>
    /// D3 (html-wallpaper-demo): the app-level keep-alive re-raise (T3) must also run while the host
    /// is attached in html mode -- exactly the same slideshow/WorkerW re-raise problem T3 fixed for
    /// video applies to html mode's own composition swapchain, since nothing else calls TryAttach on
    /// this cadence.
    /// </summary>
    [Fact]
    public void ReconcileTick_WithAnAttachedHtmlWallpaper_PostsExactlyOneKeepAliveTryAttach()
    {
        var scheduler = new Scheduler();
        var queued = new Queue<Action>();
        var host = new FakeVideoWallpaperHost();
        var player = new FakeVideoWallpaperPlayer();

        var harness = Wire(
            videoWallpaperHost: host, videoWallpaperPlayer: player, videoWallpaperPath: null,
            scheduleVideoWallpaperWork: queued.Enqueue, scheduleReconcile: scheduler.Schedule,
            wallpaperMode: WallpaperMode.Html);
        using (harness.Composition)
        {
            Assert.Single(queued);
            queued.Dequeue().Invoke();
            Assert.Equal(1, host.TryAttachCallCount);

            scheduler.Fire();

            Assert.Single(queued);
            queued.Dequeue().Invoke();
            Assert.Equal(2, host.TryAttachCallCount);
        }
    }

    /// <summary>Mirrors <see cref="ReconcileTick_WhenStartupTryPlayFailed_PostsNothing"/> for html mode: a failed startup attach must never be retried by the keep-alive tick.</summary>
    [Fact]
    public void ReconcileTick_WhenHtmlStartupAttachFailed_PostsNothing()
    {
        var scheduler = new Scheduler();
        var queued = new Queue<Action>();
        var host = new FakeVideoWallpaperHost { TryAttachReturns = false };
        var player = new FakeVideoWallpaperPlayer();

        var harness = Wire(
            videoWallpaperHost: host, videoWallpaperPlayer: player, videoWallpaperPath: null,
            scheduleVideoWallpaperWork: queued.Enqueue, scheduleReconcile: scheduler.Schedule,
            wallpaperMode: WallpaperMode.Html);
        using (harness.Composition)
        {
            queued.Dequeue().Invoke();
            Assert.Equal(1, host.TryAttachCallCount);

            scheduler.Fire();

            Assert.Empty(queued);
            Assert.Equal(1, host.TryAttachCallCount);
        }
    }

    /// <summary>
    /// F1 (video-wallpaper-review-followups, <c>R3-stale-active-after-failed-pick</c>): after
    /// <c>Stop()</c> nothing is playing, so a pick whose import fails must never leave the "active"
    /// flag on for a restore that itself fails to play -- the tick must post no keep-alive for a
    /// wallpaper that is not actually showing. The exact finding scenario ("no previous path" after
    /// a live activation) turns out unreachable: <c>currentVideoWallpaperPath</c> is only ever
    /// non-null once something has genuinely activated, so a live "active" flag always has a
    /// non-null previous path to fall back to. This is the reachable equivalent -- restoring the
    /// previous video ALSO fails to play.
    /// </summary>
    [Fact]
    public void PickingAVideo_WhenImportThrowsAndTheRestoreAlsoFailsToPlay_ReconcileTickPostsNoKeepAlive()
    {
        var scheduler = new Scheduler();
        var queued = new Queue<Action>();
        var host = new FakeVideoWallpaperHost();
        var player = new FakeVideoWallpaperPlayer();
        var path = typeof(VideoWallpaperPlaybackWiringTests).Assembly.Location;

        var harness = Wire(
            videoWallpaperHost: host, videoWallpaperPlayer: player, videoWallpaperPath: path,
            scheduleVideoWallpaperWork: queued.Enqueue, scheduleReconcile: scheduler.Schedule,
            importVideoWallpaper: _ => throw new IOException("sharing violation"));
        using (harness.Composition)
        {
            // Drains the startup activation, which genuinely plays -- the wallpaper is active and
            // has a previous path (the startup path itself) to fall back to.
            queued.Dequeue().Invoke();
            Assert.Equal(1, host.TryAttachCallCount);
            Assert.Equal(1, player.TryPlayCallCount);

            // The restore below must fail to play too, so the flag has to come down rather than
            // riding on the restore's own success.
            player.TryPlayReturns = false;

            harness.Tray.SetVideoWallpaperPath(@"C:\Users\me\Videos\clip.mp4");
            queued.Dequeue().Invoke();

            scheduler.Fire();

            Assert.Empty(queued);
        }
    }

    /// <summary>
    /// F1's other reachable variant: a first-ever pick (nothing was configured at startup, so
    /// nothing ever played) whose import fails has no previous path to restore. The flag was never
    /// set in the first place, so the tick must still post nothing -- this pins that invariant
    /// rather than leaving it to be broken silently by a later refactor.
    /// </summary>
    [Fact]
    public void FirstEverPick_WhenImportThrowsWithNoPreviousPath_ReconcileTickPostsNoKeepAlive()
    {
        var scheduler = new Scheduler();
        var queued = new Queue<Action>();
        var host = new FakeVideoWallpaperHost();
        var player = new FakeVideoWallpaperPlayer();

        var harness = Wire(
            videoWallpaperHost: host, videoWallpaperPlayer: player, videoWallpaperPath: null,
            scheduleVideoWallpaperWork: queued.Enqueue, scheduleReconcile: scheduler.Schedule,
            importVideoWallpaper: _ => throw new IOException("sharing violation"));
        using (harness.Composition)
        {
            // No startup activation was queued: nothing was configured.
            Assert.Empty(queued);

            harness.Tray.SetVideoWallpaperPath(@"C:\Users\me\Videos\clip.mp4");
            queued.Dequeue().Invoke();

            scheduler.Fire();

            Assert.Empty(queued);
            Assert.Equal(0, host.TryAttachCallCount);
        }
    }

    /// <summary>
    /// Guards against the queue piling up when the video-wallpaper thread is slower than the
    /// 400ms tick: a second tick before the first posted keep-alive has even run must not queue a
    /// second one.
    /// </summary>
    [Fact]
    public void TwoReconcileTicks_BeforeThePostedKeepAliveRuns_PostOnlyOne()
    {
        var scheduler = new Scheduler();
        var queued = new Queue<Action>();
        var host = new FakeVideoWallpaperHost();
        var player = new FakeVideoWallpaperPlayer();
        var path = typeof(VideoWallpaperPlaybackWiringTests).Assembly.Location;

        var harness = Wire(
            videoWallpaperHost: host, videoWallpaperPlayer: player, videoWallpaperPath: path,
            scheduleVideoWallpaperWork: queued.Enqueue, scheduleReconcile: scheduler.Schedule);
        using (harness.Composition)
        {
            queued.Dequeue().Invoke(); // drains startup activation

            scheduler.Fire();
            scheduler.Fire();

            Assert.Single(queued);
        }
    }

    /// <summary>
    /// V4 (video-wallpaper-http-endpoint): the HTTP route's delegate, captured through the same
    /// <c>createLocalHttpCommandServer</c> seam <see cref="HttpAlertCompositionWiringTests"/>
    /// uses. With no video-wallpaper host/player wired on this composition, the delegate answers
    /// "not available" (503 at the protocol layer) rather than touching either -- the same
    /// composition <see cref="Startup_WithNoCollaboratorsWired_DoesNotThrow"/> covers for the tray
    /// side.
    /// </summary>
    [Fact]
    public void HttpSwitch_WithNoHostOrPlayer_ReturnsFalse()
    {
        var harness = Wire(videoWallpaperHttpEnabled: true);
        using (harness.Composition)
        {
            Assert.NotNull(harness.HandleVideoWallpaperHttpSwitch);

            var accepted = harness.HandleVideoWallpaperHttpSwitch!(@"C:\Users\me\Videos\clip.mp4");

            Assert.False(accepted);
        }
    }

    /// <summary>
    /// Review 4 (R3-http-switch-no-host-branch-unproved): the test above has no dedicated thread
    /// either, so it stops at the missing-thread check. This one wires the thread and leaves only
    /// the host and player out, so the "not available" answer must come from SwitchVideoWallpaper's
    /// own null check -- and nothing may be queued.
    /// </summary>
    [Fact]
    public void HttpSwitch_WithADedicatedThreadButNoHostOrPlayer_ReturnsFalseAndQueuesNothing()
    {
        var queued = new Queue<Action>();

        var harness = Wire(scheduleVideoWallpaperWork: queued.Enqueue, videoWallpaperHttpEnabled: true);
        using (harness.Composition)
        {
            var accepted = harness.HandleVideoWallpaperHttpSwitch!(@"C:\Users\me\Videos\clip.mp4");

            Assert.False(accepted);
            Assert.Empty(queued);
        }
    }

    /// <summary>
    /// A composition with a host and player but NO dedicated video-wallpaper thread
    /// (<c>scheduleVideoWallpaperWork</c> unset) is exactly the shape
    /// <c>onVideoWallpaperThread</c> would fall back to running inline on whichever thread calls
    /// it -- fine for the tray click that shape has always served, but it would mean the import's
    /// copy runs SYNCHRONOUSLY on the HTTP server's one request thread if the delegate dispatched
    /// anyway. It must not: the delegate answers "not available" instead, and neither collaborator
    /// is ever touched -- proving the switch did not run inline on this (the test's own) thread.
    /// </summary>
    [Fact]
    public void HttpSwitch_WithHostAndPlayerButNoDedicatedThread_ReturnsFalseWithoutRunningInline()
    {
        var host = new FakeVideoWallpaperHost();
        var player = new FakeVideoWallpaperPlayer();

        var harness = Wire(
            videoWallpaperHost: host, videoWallpaperPlayer: player, videoWallpaperHttpEnabled: true);
        using (harness.Composition)
        {
            var accepted = harness.HandleVideoWallpaperHttpSwitch!(@"C:\Users\me\Videos\clip.mp4");

            Assert.False(accepted);
            Assert.Equal(0, host.TryAttachCallCount);
            Assert.Equal(0, player.TryPlayCallCount);
        }
    }

    // ---- S4 (wallpaper-scene-http-endpoint): the HTTP wallpaper-scene route's own handler ----

    /// <summary>The scene route is gated by its OWN key, independent of the video route.</summary>
    [Fact]
    public void WallpaperSceneHttpDisabled_NullSceneDelegatePassedToTheFactory()
    {
        // videoWallpaperHttpEnabled forces the shared HTTP server to actually start (and the factory
        // to actually run) so this proves the SCENE delegate specifically is null, not merely that
        // nothing was captured because the server never started.
        var harness = Wire(wallpaperSceneHttpEnabled: false, videoWallpaperHttpEnabled: true);
        using (harness.Composition)
        {
            Assert.Null(harness.HandleWallpaperSceneHttpSwitch);
        }
    }

    /// <summary>
    /// Html mode, a valid scene name, both collaborators wired: the delegate accepts (true) WITHOUT
    /// running the switch inline -- same non-blocking contract as the video route -- and only runs it
    /// (and persists) once the captured owning-thread work item is actually pumped.
    /// </summary>
    [Fact]
    public void HttpSceneSwitch_HtmlModeValidScene_DispatchesOnTheOwningThreadAndPersists()
    {
        // A List, not a Queue: Wire() itself already posts an unrelated startup work item (seeding
        // the focus border colour) onto the SAME onOwningThread scheduler, so the work item THIS
        // call posts is whichever one lands LAST, not first.
        var posted = new List<Action>();
        WallpaperScene? switched = null;
        WallpaperScene? persisted = null;

        var harness = Wire(
            wallpaperMode: WallpaperMode.Html,
            wallpaperSceneHttpEnabled: true,
            scheduleOnOwningThread: posted.Add,
            switchHtmlWallpaperScene: scene => { switched = scene; return true; },
            persistWallpaperScene: scene => persisted = scene);
        using (harness.Composition)
        {
            Assert.NotNull(harness.HandleWallpaperSceneHttpSwitch);
            var before = posted.Count;

            var accepted = harness.HandleWallpaperSceneHttpSwitch!("idle");

            Assert.True(accepted);
            Assert.Null(switched);
            Assert.Null(persisted);
            Assert.Equal(before + 1, posted.Count);

            posted[^1]();

            Assert.Equal(WallpaperScene.Idle, switched);
            Assert.Equal(WallpaperScene.Idle, persisted);
        }
    }

    /// <summary>Video mode: there is no scene to switch, so the delegate answers false and dispatches nothing.</summary>
    [Fact]
    public void HttpSceneSwitch_VideoMode_ReturnsFalseAndDispatchesNothing()
    {
        var posted = new List<Action>();
        var switchCalls = 0;

        var harness = Wire(
            wallpaperMode: WallpaperMode.Video,
            wallpaperSceneHttpEnabled: true,
            scheduleOnOwningThread: posted.Add,
            switchHtmlWallpaperScene: _ => { switchCalls++; return true; });
        using (harness.Composition)
        {
            var before = posted.Count;

            var accepted = harness.HandleWallpaperSceneHttpSwitch!("idle");

            Assert.False(accepted);
            Assert.Equal(before, posted.Count);
            Assert.Equal(0, switchCalls);
        }
    }

    /// <summary>
    /// Html mode but no alert layer wired (switchHtmlWallpaperScene left null, its production default):
    /// the handler must know availability, exactly like <see cref="_htmlWallpaperMode"/> alone is not
    /// enough -- WireProduction only ever supplies this delegate when an alert layer exists.
    /// </summary>
    [Fact]
    public void HttpSceneSwitch_HtmlModeWithNoSwitchDelegateWired_ReturnsFalse()
    {
        var harness = Wire(wallpaperMode: WallpaperMode.Html, wallpaperSceneHttpEnabled: true);
        using (harness.Composition)
        {
            var accepted = harness.HandleWallpaperSceneHttpSwitch!("idle");

            Assert.False(accepted);
        }
    }

    /// <summary>
    /// Defensive: <see cref="WallpaperSceneHttpProtocol.TryValidate"/> already restricts the body to
    /// the closed allow-list before this delegate is ever called in production, so this name should be
    /// unreachable there -- but the delegate's own contract must not assume its caller's validation
    /// forever.
    /// </summary>
    [Fact]
    public void HttpSceneSwitch_AnUnknownSceneName_ReturnsFalseWithoutDispatching()
    {
        var posted = new List<Action>();
        var switchCalls = 0;

        var harness = Wire(
            wallpaperMode: WallpaperMode.Html,
            wallpaperSceneHttpEnabled: true,
            scheduleOnOwningThread: posted.Add,
            switchHtmlWallpaperScene: _ => { switchCalls++; return true; });
        using (harness.Composition)
        {
            var before = posted.Count;

            var accepted = harness.HandleWallpaperSceneHttpSwitch!("not-a-scene");

            Assert.False(accepted);
            Assert.Equal(before, posted.Count);
            Assert.Equal(0, switchCalls);
        }
    }

    /// <summary>Persist only runs once the underlying switch actually reports success.</summary>
    [Fact]
    public void HttpSceneSwitch_UnderlyingSwitchReturnsFalse_DoesNotPersist()
    {
        var posted = new List<Action>();
        var persistCalls = 0;

        var harness = Wire(
            wallpaperMode: WallpaperMode.Html,
            wallpaperSceneHttpEnabled: true,
            scheduleOnOwningThread: posted.Add,
            switchHtmlWallpaperScene: _ => false,
            persistWallpaperScene: _ => persistCalls++);
        using (harness.Composition)
        {
            var before = posted.Count;

            var accepted = harness.HandleWallpaperSceneHttpSwitch!("raphael");
            Assert.True(accepted); // dispatch was accepted; the posted work item decides the rest
            Assert.Equal(before + 1, posted.Count);

            posted[^1]();

            Assert.Equal(0, persistCalls);
        }
    }

    /// <summary>
    /// S6 (wallpaper-scene-http-endpoint, R3-owning-thread-work-unguarded): before this fix, the
    /// posted work item called <c>switchHtmlWallpaperScene</c> and <c>persistWallpaperScene</c> with
    /// no exception guard at all, AFTER the HTTP 202 reply already went out -- a throw here had
    /// nowhere left to go but the owning dispatcher. This proves a throwing switch (1) never escapes
    /// the posted work, (2) never persists (there is nothing successful to remember), and (3) is
    /// reported through <c>desktopTrace</c> with the SAME type-name-only shape <see
    /// cref="HttpSwitch_WhenImportThrows_TracesPhaseHttpAndRestoresThePreviousVideoWithoutPersisting"/>
    /// already proves for the video route's own import failure -- never the exception message.
    /// </summary>
    [Fact]
    public void HttpSceneSwitch_WhenSwitchThrows_ReportsAndDoesNotPersist()
    {
        var posted = new List<Action>();
        var trace = new RecordingDesktopTrace();
        var persistCalls = 0;

        var harness = Wire(
            wallpaperMode: WallpaperMode.Html,
            wallpaperSceneHttpEnabled: true,
            scheduleOnOwningThread: posted.Add,
            desktopTrace: trace,
            switchHtmlWallpaperScene: _ => throw new InvalidOperationException("boom"),
            persistWallpaperScene: _ => persistCalls++);
        using (harness.Composition)
        {
            var before = posted.Count;

            var accepted = harness.HandleWallpaperSceneHttpSwitch!("idle");
            Assert.True(accepted);
            Assert.Equal(before + 1, posted.Count);

            var exception = Record.Exception(() => posted[^1]());

            Assert.Null(exception);
            Assert.Equal(0, persistCalls);
            Assert.Contains(
                "wallpaper-scene-http switch-failed error=InvalidOperationException", trace.Lines);
        }
    }

    /// <summary>
    /// S6 (wallpaper-scene-http-endpoint, R3-owning-thread-work-unguarded): the OTHER half of the
    /// same posted work item -- the switch itself succeeds, but <c>persistWallpaperScene</c> throws.
    /// That must not escape either, and is reported under its own distinct trace shape so it never
    /// reads as a switch failure when it was the settings write that actually failed.
    /// </summary>
    [Fact]
    public void HttpSceneSwitch_WhenPersistThrows_DoesNotPropagate()
    {
        var posted = new List<Action>();
        var trace = new RecordingDesktopTrace();
        WallpaperScene? switched = null;

        var harness = Wire(
            wallpaperMode: WallpaperMode.Html,
            wallpaperSceneHttpEnabled: true,
            scheduleOnOwningThread: posted.Add,
            desktopTrace: trace,
            switchHtmlWallpaperScene: scene => { switched = scene; return true; },
            persistWallpaperScene: _ => throw new IOException("disk full"));
        using (harness.Composition)
        {
            var before = posted.Count;

            var accepted = harness.HandleWallpaperSceneHttpSwitch!("raphael");
            Assert.True(accepted);
            Assert.Equal(before + 1, posted.Count);

            var exception = Record.Exception(() => posted[^1]());

            Assert.Null(exception);
            Assert.Equal(WallpaperScene.Raphael, switched);
            Assert.Contains("wallpaper-scene-http persist-failed error=IOException", trace.Lines);
        }
    }

    /// <summary>
    /// With a dedicated video-wallpaper thread wired (the shape every real composition uses, per
    /// <c>AppComposition.WireProduction</c>), the delegate returns <see langword="true"/> the
    /// instant the work is QUEUED -- before the scheduler ever runs it -- and the switch that
    /// eventually runs is the exact same stop/import/persist/activate sequence the tray's own
    /// pick uses, proven here by running it through the SAME <c>importVideoWallpaper</c> seam and
    /// observing the SAME host/player call sequence <see
    /// cref="PickingAVideo_StopsPlaybackBeforeImportingBeforeReattachingAndReplaying"/> proves for
    /// the tray.
    /// </summary>
    [Fact]
    public void HttpSwitch_WithDedicatedThread_ReturnsTrueAtOnceAndRunsOnlyOnceTheSchedulerDrainsIt()
    {
        var queued = new Queue<Action>();
        var host = new FakeVideoWallpaperHost();
        var player = new FakeVideoWallpaperPlayer();
        var imported = typeof(VideoWallpaperPlaybackWiringTests).Assembly.Location;
        var trace = new RecordingDesktopTrace();

        var harness = Wire(
            videoWallpaperHost: host, videoWallpaperPlayer: player,
            scheduleVideoWallpaperWork: queued.Enqueue, importVideoWallpaper: _ => imported,
            desktopTrace: trace, videoWallpaperHttpEnabled: true);
        using (harness.Composition)
        {
            // Clears the "http-server start requested" line construction itself just traced, so
            // the assertion below reads only the switch's own line.
            trace.Lines.Clear();

            var accepted = harness.HandleVideoWallpaperHttpSwitch!(@"C:\Users\me\Videos\clip.mp4");

            // Accepted for dispatch, and QUEUED, not run -- on the caller's own thread (this test
            // method) nothing has attached or played yet.
            Assert.True(accepted);
            Assert.Equal(0, host.TryAttachCallCount);
            Assert.Equal(0, player.TryPlayCallCount);
            Assert.Single(queued);

            queued.Dequeue().Invoke();

            Assert.Equal(1, host.TryAttachCallCount);
            Assert.Equal(1, player.TryPlayCallCount);
            Assert.Equal(imported, player.LastVideoPath);
            Assert.Equal(
                ["video-wallpaper phase=http pathExists=True tryAttach=True tryPlay=True"],
                trace.Lines);
        }
    }

    /// <summary>
    /// The failure/restore contract the tray already gets
    /// (<see cref="PickingAVideo_WhenImportThrows_TracesAndRestoresThePreviousVideoWithoutPersisting"/>)
    /// applies identically to an HTTP-initiated switch: no absolute path in the trace, the
    /// previous video restored, and nothing persisted -- only the phase on the failure line reads
    /// <c>http</c> instead of <c>pick</c>. The restore itself stays <c>phase=restore</c> either
    /// way, since restoring is the same fallback regardless of who triggered the switch.
    /// </summary>
    [Fact]
    public void HttpSwitch_WhenImportThrows_TracesPhaseHttpAndRestoresThePreviousVideoWithoutPersisting()
    {
        var queued = new Queue<Action>();
        var host = new FakeVideoWallpaperHost();
        var player = new FakeVideoWallpaperPlayer();
        var trace = new RecordingDesktopTrace();
        var persisted = new List<string>();
        var previousPath = typeof(VideoWallpaperPlaybackWiringTests).Assembly.Location;

        var harness = Wire(
            videoWallpaperHost: host, videoWallpaperPlayer: player, videoWallpaperPath: previousPath,
            scheduleVideoWallpaperWork: queued.Enqueue, desktopTrace: trace,
            persistVideoWallpaperPath: persisted.Add, videoWallpaperHttpEnabled: true,
            importVideoWallpaper: _ => throw new IOException("sharing violation"));
        using (harness.Composition)
        {
            // Startup queued its own activation work item ahead of anything this test posts.
            queued.Dequeue().Invoke();
            trace.Lines.Clear();

            var accepted = harness.HandleVideoWallpaperHttpSwitch!(@"C:\Users\me\Videos\clip.mp4");
            Assert.True(accepted);
            queued.Dequeue().Invoke();

            Assert.Contains("video-wallpaper phase=http import-failed error=IOException", trace.Lines);
            Assert.Contains(
                trace.Lines,
                line => line.StartsWith("video-wallpaper phase=restore") && line.Contains("tryPlay=True"));
            Assert.Empty(persisted);
        }
    }

    /// <summary>
    /// Both entry points share one composition without interfering: a tray pick still traces
    /// <c>phase=pick</c> and an HTTP switch still traces <c>phase=http</c>, proving V4 did not
    /// collapse the two or leave the tray's own phase hard-coded to the wrong word.
    /// </summary>
    [Fact]
    public void TrayPickAndHttpSwitch_ShareOneCompositionButTraceTheirOwnDistinctPhase()
    {
        var queued = new Queue<Action>();
        var host = new FakeVideoWallpaperHost();
        var player = new FakeVideoWallpaperPlayer();
        var trace = new RecordingDesktopTrace();

        var harness = Wire(
            videoWallpaperHost: host, videoWallpaperPlayer: player,
            scheduleVideoWallpaperWork: queued.Enqueue, desktopTrace: trace,
            videoWallpaperHttpEnabled: true);
        using (harness.Composition)
        {
            harness.Tray.SetVideoWallpaperPath(@"C:\Users\me\Videos\tray.mp4");
            queued.Dequeue().Invoke();

            harness.HandleVideoWallpaperHttpSwitch!(@"C:\Users\me\Videos\http.mp4");
            queued.Dequeue().Invoke();

            Assert.Contains(trace.Lines, l => l.StartsWith("video-wallpaper phase=pick "));
            Assert.Contains(trace.Lines, l => l.StartsWith("video-wallpaper phase=http "));
            Assert.DoesNotContain(trace.Lines, l => l.Contains("tray.mp4", StringComparison.Ordinal));
            Assert.DoesNotContain(trace.Lines, l => l.Contains("http.mp4", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// same-video-noop, S2 (snapshot seam per noop-followups F1): a repeat HTTP request naming the
    /// video that is already playing must be a true no-op -- no <c>Stop()</c>, no import, no
    /// persist, and no second <c>ActivateVideoWallpaper</c> call -- while still answering
    /// "accepted" (202 at the protocol layer) exactly like any other switch this delegate
    /// dispatches. <c>readVideoFileSnapshot</c> is stubbed to always return the same constant
    /// snapshot regardless of path, so this proves the WIRING (the check runs, and short-circuits
    /// before <c>Stop()</c>), not the real file-reading comparison <see
    /// cref="VideoWallpaperImportTests.TryReadSnapshot_HardLinkedFiles_ReturnsEqualSnapshots"/>
    /// already covers.
    /// </summary>
    [Fact]
    public void HttpSwitch_SamePathWhileActive_SkipsStopImportAndPersistButStillReturnsTrue()
    {
        var queued = new Queue<Action>();
        var host = new FakeVideoWallpaperHost();
        var player = new FakeVideoWallpaperPlayer();
        var trace = new RecordingDesktopTrace();
        var imports = new List<string>();
        var persisted = new List<string>();
        var path = typeof(VideoWallpaperPlaybackWiringTests).Assembly.Location;
        var constantSnapshot = new VideoWallpaperImport.VideoFileSnapshot(1, 1, 1, 1);

        var harness = Wire(
            videoWallpaperHost: host, videoWallpaperPlayer: player, videoWallpaperPath: path,
            importVideoWallpaper: p =>
            {
                imports.Add(p);
                return p;
            },
            persistVideoWallpaperPath: persisted.Add, desktopTrace: trace,
            scheduleVideoWallpaperWork: queued.Enqueue,
            videoWallpaperHttpEnabled: true, readVideoFileSnapshot: _ => constantSnapshot);
        using (harness.Composition)
        {
            // Startup queued its own activation work item ahead of anything this test posts.
            queued.Dequeue().Invoke();
            Assert.Equal(1, player.TryPlayCallCount);
            trace.Lines.Clear();

            var accepted = harness.HandleVideoWallpaperHttpSwitch!(path);
            Assert.True(accepted);
            Assert.Single(queued);
            queued.Dequeue().Invoke();

            Assert.Equal(0, player.StopCallCount);
            Assert.Empty(imports);
            Assert.Empty(persisted);
            // No second attach/play: TryPlayCallCount is still exactly the one from startup.
            Assert.Equal(1, player.TryPlayCallCount);
            Assert.Equal(["video-wallpaper phase=http unchanged"], trace.Lines);
        }
    }

    /// <summary>
    /// The skip above requires playback to actually BE active, not merely configured: when startup
    /// attached but <see cref="IVideoWallpaperPlayer.TryPlay"/> failed, <c>videoWallpaperActive</c>
    /// is false, and a repeat request for the very same video must revive it exactly like today --
    /// the constraint from the approach doc ("if playback died, a repeat request revives it").
    /// </summary>
    [Fact]
    public void HttpSwitch_SamePathWhilePlaybackIsNotActive_ReloadsAnyway()
    {
        var queued = new Queue<Action>();
        var host = new FakeVideoWallpaperHost();
        var player = new FakeVideoWallpaperPlayer { TryPlayReturns = false };
        var trace = new RecordingDesktopTrace();
        var imports = new List<string>();
        var path = typeof(VideoWallpaperPlaybackWiringTests).Assembly.Location;

        var harness = Wire(
            videoWallpaperHost: host, videoWallpaperPlayer: player, videoWallpaperPath: path,
            importVideoWallpaper: p =>
            {
                imports.Add(p);
                return p;
            },
            desktopTrace: trace, scheduleVideoWallpaperWork: queued.Enqueue,
            videoWallpaperHttpEnabled: true,
            readVideoFileSnapshot: _ => new VideoWallpaperImport.VideoFileSnapshot(1, 1, 1, 1));
        using (harness.Composition)
        {
            // Startup attached but TryPlay failed -- videoWallpaperActive is false.
            queued.Dequeue().Invoke();
            Assert.Equal(1, player.TryPlayCallCount);
            trace.Lines.Clear();

            var accepted = harness.HandleVideoWallpaperHttpSwitch!(path);
            Assert.True(accepted);
            queued.Dequeue().Invoke();

            Assert.Equal(1, player.StopCallCount);
            Assert.Equal([path], imports);
            Assert.DoesNotContain(trace.Lines, l => l.Contains("unchanged", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// A genuinely different video (<c>readVideoFileSnapshot</c> stubbed to return no snapshot at
    /// all, standing in for two distinct files) always reloads -- the ordinary switch this whole
    /// feature must leave untouched.
    /// </summary>
    [Fact]
    public void HttpSwitch_DifferentPathWhileActive_Reloads()
    {
        var queued = new Queue<Action>();
        var host = new FakeVideoWallpaperHost();
        var player = new FakeVideoWallpaperPlayer();
        var trace = new RecordingDesktopTrace();
        var imports = new List<string>();
        var persisted = new List<string>();
        var previousPath = typeof(VideoWallpaperPlaybackWiringTests).Assembly.Location;
        const string requestedPath = @"C:\Users\me\Videos\different.mp4";
        const string imported = @"C:\LOCALAPPDATA\CosmicWin\video-wallpaper.mp4";

        var harness = Wire(
            videoWallpaperHost: host, videoWallpaperPlayer: player, videoWallpaperPath: previousPath,
            importVideoWallpaper: p =>
            {
                imports.Add(p);
                return imported;
            },
            persistVideoWallpaperPath: persisted.Add, desktopTrace: trace,
            scheduleVideoWallpaperWork: queued.Enqueue,
            videoWallpaperHttpEnabled: true, readVideoFileSnapshot: _ => null);
        using (harness.Composition)
        {
            // Startup queued its own activation work item ahead of anything this test posts.
            queued.Dequeue().Invoke();
            trace.Lines.Clear();

            var accepted = harness.HandleVideoWallpaperHttpSwitch!(requestedPath);
            Assert.True(accepted);
            queued.Dequeue().Invoke();

            Assert.Equal(1, player.StopCallCount);
            Assert.Equal([requestedPath], imports);
            Assert.Equal([imported], persisted);
            Assert.DoesNotContain(trace.Lines, l => l.Contains("unchanged", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Review finding R3-predicate-args-unproved, adapted to the snapshot seam (noop-followups F1):
    /// the tests above stub <c>readVideoFileSnapshot</c> with a constant, so they would still pass
    /// if the skip compared the wrong operands. Here the fake reader is keyed by PATH and returns a
    /// DIFFERENT snapshot for every distinct path except <c>repeatSource</c>, which is scripted to
    /// return the SAME snapshot as <c>imported</c> -- standing in for a hard-linked alias of the
    /// currently playing file. The recorded query order proves the compare fetches a fresh read of
    /// the REQUESTED path each time (never a cached one), and that the value it is compared AGAINST
    /// -- <c>currentVideoSnapshot</c> -- was captured from the IMPORTED destination the first switch
    /// actually activated, never from the raw source the caller passed in: the trap the feature doc
    /// names, that the caller's source is never what is stored.
    /// </summary>
    [Fact]
    public void HttpSwitch_ComparesTheRequestedPathAgainstTheCurrentImportedPath()
    {
        var queued = new Queue<Action>();
        var host = new FakeVideoWallpaperHost();
        var player = new FakeVideoWallpaperPlayer();
        var trace = new RecordingDesktopTrace();
        var imports = new List<string>();
        var queriedPaths = new List<string>();
        var startupPath = typeof(VideoWallpaperPlaybackWiringTests).Assembly.Location;
        const string firstSource = @"C:\Users\me\Videos\first.mp4";
        const string imported = @"C:\LOCALAPPDATA\CosmicWin\video-wallpaper.mp4";
        const string repeatSource = @"C:\Users\me\Videos\first-by-another-name.mp4";
        var snapshotsByPath = new Dictionary<string, VideoWallpaperImport.VideoFileSnapshot>
        {
            [startupPath] = new(1, 1, 1, 1),
            [firstSource] = new(2, 2, 2, 2),
            [imported] = new(3, 3, 3, 3),
            [repeatSource] = new(3, 3, 3, 3), // scripted to match `imported` -- a hard-linked alias.
        };

        var harness = Wire(
            videoWallpaperHost: host, videoWallpaperPlayer: player, videoWallpaperPath: startupPath,
            importVideoWallpaper: p =>
            {
                imports.Add(p);
                return imported;
            },
            desktopTrace: trace, scheduleVideoWallpaperWork: queued.Enqueue,
            videoWallpaperHttpEnabled: true,
            readVideoFileSnapshot: path =>
            {
                queriedPaths.Add(path);
                return snapshotsByPath.TryGetValue(path, out var snapshot) ? snapshot : null;
            });
        using (harness.Composition)
        {
            // Startup queued its own activation work item ahead of anything this test posts.
            queued.Dequeue().Invoke();

            Assert.True(harness.HandleVideoWallpaperHttpSwitch!(firstSource));
            queued.Dequeue().Invoke();
            Assert.Equal(1, player.StopCallCount);
            trace.Lines.Clear();

            Assert.True(harness.HandleVideoWallpaperHttpSwitch!(repeatSource));
            queued.Dequeue().Invoke();

            // startup snapshots startupPath; the first switch's skip-check reads firstSource (a
            // miss against startupPath's snapshot) then, once it actually activates, snapshots
            // `imported` -- never firstSource itself; the repeat switch's skip-check reads
            // repeatSource, which matches that cached `imported` snapshot.
            Assert.Equal([startupPath, firstSource, imported, repeatSource], queriedPaths);
            Assert.Equal(1, player.StopCallCount);
            Assert.Equal([firstSource], imports);
            Assert.Equal(["video-wallpaper phase=http unchanged"], trace.Lines);
        }
    }

    /// <summary>
    /// F1 (noop-followups, R3-inplace-edit-hardlink): the whole reason this task replaces a
    /// same-file-identity check with a snapshot comparison. The fake reader returns two DIFFERENT
    /// snapshots in sequence for the SAME path/identity -- the first captured when playback starts
    /// (via <c>ActivateVideoWallpaper</c>'s own <c>SafeReadSnapshot</c> call), the second read fresh
    /// when the repeat HTTP request's skip-check runs, standing in for a video re-encoded in place
    /// between the two moments. A same-identity-but-different-size/last-write pair must never skip.
    /// </summary>
    [Fact]
    public void HttpSwitch_FileEditedInPlaceSincePlaybackStarted_Reloads()
    {
        var queued = new Queue<Action>();
        var host = new FakeVideoWallpaperHost();
        var player = new FakeVideoWallpaperPlayer();
        var trace = new RecordingDesktopTrace();
        var path = typeof(VideoWallpaperPlaybackWiringTests).Assembly.Location;
        var snapshots = new Queue<VideoWallpaperImport.VideoFileSnapshot?>(
        [
            new VideoWallpaperImport.VideoFileSnapshot(1, 1, 100, 100), // captured at playback start.
            new VideoWallpaperImport.VideoFileSnapshot(1, 1, 999, 100), // same identity, edited size.
        ]);

        var harness = Wire(
            videoWallpaperHost: host, videoWallpaperPlayer: player, videoWallpaperPath: path,
            desktopTrace: trace, scheduleVideoWallpaperWork: queued.Enqueue,
            videoWallpaperHttpEnabled: true,
            readVideoFileSnapshot: _ => snapshots.Count > 0 ? snapshots.Dequeue() : null);
        using (harness.Composition)
        {
            // Startup activation consumes the first queued snapshot.
            queued.Dequeue().Invoke();
            trace.Lines.Clear();

            var accepted = harness.HandleVideoWallpaperHttpSwitch!(path);
            Assert.True(accepted);
            queued.Dequeue().Invoke();

            Assert.Equal(1, player.StopCallCount);
            Assert.DoesNotContain(trace.Lines, l => l.Contains("unchanged", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Review finding R3-snapshot-after-play-window: the baseline snapshot must be read BEFORE the
    /// player opens the file. The fake reader reports a size equal to how many times TryPlay has
    /// run, standing in for an in-place edit that lands while the player is opening the file. Read
    /// after TryPlay, that edit becomes the baseline and the repeat request is wrongly skipped as
    /// unchanged; read before, the baseline predates the edit and the repeat request reloads -- the
    /// fail-safe direction.
    /// </summary>
    [Fact]
    public void HttpSwitch_FileEditedWhilePlaybackOpensIt_ReloadsOnTheRepeatRequest()
    {
        var queued = new Queue<Action>();
        var host = new FakeVideoWallpaperHost();
        var player = new FakeVideoWallpaperPlayer();
        var trace = new RecordingDesktopTrace();
        var path = typeof(VideoWallpaperPlaybackWiringTests).Assembly.Location;

        var harness = Wire(
            videoWallpaperHost: host, videoWallpaperPlayer: player, videoWallpaperPath: path,
            desktopTrace: trace, scheduleVideoWallpaperWork: queued.Enqueue,
            videoWallpaperHttpEnabled: true,
            readVideoFileSnapshot: _ => new VideoWallpaperImport.VideoFileSnapshot(1, 1, player.TryPlayCallCount, 100));
        using (harness.Composition)
        {
            // Startup activation: the only TryPlay so far.
            queued.Dequeue().Invoke();
            Assert.Equal(1, player.TryPlayCallCount);
            trace.Lines.Clear();

            Assert.True(harness.HandleVideoWallpaperHttpSwitch!(path));
            queued.Dequeue().Invoke();

            Assert.Equal(1, player.StopCallCount);
            // R3-reload-completion-unasserted: the reload actually played again, not only skipped the skip.
            Assert.Equal(2, player.TryPlayCallCount);
            Assert.DoesNotContain(trace.Lines, l => l.Contains("unchanged", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// F1 (noop-followups, R3-predicate-throw-not-contained): an injected reader that throws must
    /// never escape the video wallpaper work item -- it reads as "no snapshot", exactly like a real
    /// <see cref="VideoWallpaperImport.TryReadSnapshot"/> failure, and the switch reloads.
    /// </summary>
    [Fact]
    public void HttpSwitch_ThrowingSnapshotReader_ReloadsWithoutTheExceptionEscaping()
    {
        var queued = new Queue<Action>();
        var host = new FakeVideoWallpaperHost();
        var player = new FakeVideoWallpaperPlayer();
        var trace = new RecordingDesktopTrace();
        var path = typeof(VideoWallpaperPlaybackWiringTests).Assembly.Location;
        var reads = 0;

        var harness = Wire(
            videoWallpaperHost: host, videoWallpaperPlayer: player, videoWallpaperPath: path,
            desktopTrace: trace, scheduleVideoWallpaperWork: queued.Enqueue,
            videoWallpaperHttpEnabled: true,
            readVideoFileSnapshot: _ =>
            {
                reads++;
                // The first read (startup) succeeds and becomes the cached snapshot; every read
                // after that (the repeat request's skip-check) throws.
                return reads == 1
                    ? new VideoWallpaperImport.VideoFileSnapshot(1, 1, 1, 1)
                    : throw new IOException("boom");
            });
        using (harness.Composition)
        {
            queued.Dequeue().Invoke();
            trace.Lines.Clear();

            var accepted = harness.HandleVideoWallpaperHttpSwitch!(path);
            var thrown = Record.Exception(() => queued.Dequeue().Invoke());

            Assert.True(accepted);
            Assert.Null(thrown);
            Assert.Equal(1, player.StopCallCount);
            Assert.DoesNotContain(trace.Lines, l => l.Contains("unchanged", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Decision 1 in <c>odd/tasks/same-video-noop.md</c>: HTTP only. A tray re-pick of the very
    /// same video must keep reloading exactly as today, even with <c>readVideoFileSnapshot</c>
    /// stubbed to always agree -- <c>setVideoWallpaperPath</c>'s tray call site never passes
    /// <c>skipIfUnchanged</c>, so the reader is never even consulted on that path.
    /// </summary>
    [Fact]
    public void TrayPick_SamePathWhileActive_ReloadsAnyway()
    {
        var host = new FakeVideoWallpaperHost();
        var player = new FakeVideoWallpaperPlayer();
        var imports = new List<string>();
        var path = typeof(VideoWallpaperPlaybackWiringTests).Assembly.Location;

        var harness = Wire(
            videoWallpaperHost: host, videoWallpaperPlayer: player, videoWallpaperPath: path,
            importVideoWallpaper: p =>
            {
                imports.Add(p);
                return p;
            },
            readVideoFileSnapshot: _ => new VideoWallpaperImport.VideoFileSnapshot(1, 1, 1, 1));
        using (harness.Composition)
        {
            Assert.Equal(1, player.TryPlayCallCount);

            harness.Tray.SetVideoWallpaperPath(path);

            Assert.Equal(1, player.StopCallCount);
            Assert.Equal([path], imports);
        }
    }
}
