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

    private sealed record Harness(AppComposition Composition, TrayMenuController Tray);

    private static Harness Wire(
        IVideoWallpaperHost? videoWallpaperHost = null,
        IVideoWallpaperPlayer? videoWallpaperPlayer = null,
        string? videoWallpaperPath = null,
        Func<string, string>? importVideoWallpaper = null,
        CosmicWin.App.Diagnostics.IDesktopTrace? desktopTrace = null,
        Action<Action>? scheduleVideoWallpaperWork = null,
        Action? disposeVideoWallpaper = null,
        Action<string>? persistVideoWallpaperPath = null,
        Func<TimeSpan, Action, IDisposable>? scheduleReconcile = null)
    {
        var workspace = new FakeWorkspace();
        var primary = new FakeDisplay(
            new IntPtr(1), Rectangle.FromSize(0, 0, 1920, 1080), Rectangle.FromSize(0, 0, 1920, 1080), 1.0, true);
        var registry = new WindowRegistry();
        var treeManager = new TreeManager([primary], primary, registry);
        var foreground = new NoForeground();
        TrayMenuController? tray = null;

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
            videoWallpaperHost: videoWallpaperHost,
            videoWallpaperPlayer: videoWallpaperPlayer,
            scheduleVideoWallpaperWork: scheduleVideoWallpaperWork,
            disposeVideoWallpaper: disposeVideoWallpaper,
            videoWallpaperPath: videoWallpaperPath,
            persistVideoWallpaperPath: persistVideoWallpaperPath);

        return new Harness(composition, tray!);
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
}
