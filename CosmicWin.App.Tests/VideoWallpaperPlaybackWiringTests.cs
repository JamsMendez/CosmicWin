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

        public bool TryPlay(IVideoWallpaperHost host, string videoPath)
        {
            TryPlayCallCount++;
            LastHost = host;
            LastVideoPath = videoPath;
            events?.Add("player.TryPlay");
            return TryPlayReturns;
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
        Action? disposeVideoWallpaper = null)
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
            scheduleReconcile: (_, _) => new NullDisposable(),
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
            videoWallpaperPath: videoWallpaperPath);

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
}
