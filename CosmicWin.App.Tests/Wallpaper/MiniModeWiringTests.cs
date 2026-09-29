using System.Runtime.CompilerServices;
using CosmicWin.App.Alerts;
using CosmicWin.App.Input;
using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.App.Tray;
using CosmicWin.App.Wallpaper;
using CosmicWin.Interop;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;
using Windows.Win32.Graphics.Direct3D11;

namespace CosmicWin.App.Tests.Wallpaper;

/// <summary>
/// T4 (mini-scene-window): <c>wallpaper-mode=mini</c> wired through <see cref="AppComposition.Wire"/>.
/// The mini window replaces the wallpaper host, the player and the wallpaper alert layer, so these
/// facts pin what the composition does with a fake <see cref="IMiniSceneWindow"/> and prove the
/// video/html paths stay untouched.
/// </summary>
public sealed class MiniModeWiringTests
{
    private sealed class FakeMiniWindow : IMiniSceneWindow
    {
        public List<(WallpaperScene Scene, int Fps, Rect Bounds)> Shown { get; } = [];
        public List<Rect> Moves { get; } = [];
        public List<WallpaperScene> Switched { get; } = [];
        public List<AlertShowRequest> Alerts { get; } = [];
        public int Hides { get; private set; }
        public int Disposed { get; private set; }
        public bool ShowResult { get; set; } = true;
        public bool SwitchResult { get; set; } = true;
        public bool ThrowOnShow { get; set; }

        public bool Show(WallpaperScene scene, int fps, Rect bounds)
        {
            if (ThrowOnShow) throw new InvalidOperationException("boom");
            Shown.Add((scene, fps, bounds));
            return ShowResult;
        }

        public bool SwitchScene(WallpaperScene scene)
        {
            Switched.Add(scene);
            return SwitchResult;
        }

        public void MoveTo(Rect bounds) => Moves.Add(bounds);

        public void ShowAlert(AlertShowRequest request) => Alerts.Add(request);

        public void HideAlert() => Hides++;

        public void Dispose() => Disposed++;
    }

    private sealed class Disposable : IDisposable { public void Dispose() { } }

    private sealed class Foreground : IForegroundWindowSource { public nint GetForegroundHandle() => 0; }

    private sealed class Scheduler
    {
        private Action? _tick;

        public IDisposable Schedule(TimeSpan interval, Action tick)
        {
            _tick = tick;
            return new Disposable();
        }

        public void Tick() => _tick!();
    }

    private sealed class Server(Func<string, string> handle) : IAlertCommandServer
    {
        public void Start() { }
        public void Dispose() { }
        public string Send(string command) => handle(command);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class RecordingTrace : CosmicWin.App.Diagnostics.IDesktopTrace
    {
        public List<string> Lines { get; } = [];
        public void Record(string line) => Lines.Add(line);
    }

    private sealed class Host : IVideoWallpaperHost
    {
        public int Attaches { get; private set; }
        public bool TryAttach() { Attaches++; return true; }
        ID3D11Device IVideoWallpaperHost.Device => throw new NotSupportedException();
        ID3D11Texture2D IVideoWallpaperHost.GetBackBuffer() => throw new NotSupportedException();
        public void Present() { }
        public (int Width, int Height) BackBufferSize => (0, 0);
        public void SetVideoTransform(float centerX, float centerY, float offsetX, float offsetY, float angleDegrees, float scale) { }
        public void ClearVideoTransform() { }
        public void Dispose() { }
    }

    private sealed class Player : IVideoWallpaperPlayer
    {
        public int Plays { get; private set; }
        public bool TryPlay(IVideoWallpaperHost host, string path) { Plays++; return true; }
        public void Stop() { }
        public void Dispose() { }
    }

    private sealed class Harness
    {
        public required AppComposition Composition { get; init; }
        public required TrayMenuController Tray { get; init; }
        public required Scheduler Timer { get; init; }
        public required FakeDisplay Display { get; init; }
        public required ManualClock Clock { get; init; }
        public required RecordingTrace Trace { get; init; }
        public required List<Action> Posted { get; init; }
        public required List<string> Imports { get; init; }
        public required List<WallpaperScene> PersistedScenes { get; init; }
        public required List<MiniCorner> PersistedCorners { get; init; }
        public required FakeKeyboardHookPlatform Platform { get; init; }
        public Func<string, bool>? SceneSwitch { get; init; }
        public Server? AlertServer { get; init; }
    }

    // The primary display: 3440x1440 with a 40px taskbar at the bottom.
    private static readonly Rectangle Bounds = Rectangle.FromSize(0, 0, 3440, 1440);
    private static readonly Rectangle WorkArea = Rectangle.FromSize(0, 0, 3440, 1400);

    private static Harness Wire(
        WallpaperMode mode,
        IMiniSceneWindow? miniWindow = null,
        MiniCorner corner = MiniCorner.TopRight,
        WallpaperScene scene = WallpaperScene.Raphael,
        int fps = 30,
        bool queueOwningThread = false,
        bool alerts = false,
        IVideoWallpaperHost? host = null,
        IVideoWallpaperPlayer? player = null,
        string? videoPath = null,
        Func<IReadOnlyList<IDisplay>>? refreshDisplays = null,
        Func<WallpaperScene, bool>? htmlSwitch = null)
    {
        var display = new FakeDisplay((nint)1, Bounds, WorkArea, 1.0, true);
        var timer = new Scheduler();
        var clock = new ManualClock();
        var trace = new RecordingTrace();
        var posted = new List<Action>();
        TrayMenuController? tray = null;
        Func<string, bool>? sceneSwitch = null;
        Server? server = null;
        var imports = new List<string>();
        var scenes = new List<WallpaperScene>();
        var corners = new List<MiniCorner>();
        var platform = new FakeKeyboardHookPlatform();

        var composition = AppComposition.Wire(
            new FakeWorkspace(),
            new TreeManager([display], display, new WindowRegistry()), new WindowRegistry(),
            new Foreground(), new ExceptionListStore(ExceptionList.Empty), new RecordingFocusTrace(),
            () => { }, timer.Schedule,
            writer => new LowLevelKeyboardHook(writer, platform, TimeSpan.FromSeconds(5), () => 0),
            () => ExceptionList.Empty, () => { },
            controller => { tray = controller; return new Disposable(); },
            path => { imports.Add(path); return path; },
            desktopTrace: trace,
            scheduleOnOwningThread: queueOwningThread ? posted.Add : null,
            alertsEnabled: alerts,
            createAlertCommandServer: (_, handle, _) => server = new Server(handle),
            videoWallpaperHost: host,
            videoWallpaperPlayer: player,
            videoWallpaperPath: videoPath,
            scheduleVideoWallpaperWork: work => work(),
            wallpaperMode: mode,
            wallpaperSceneHttpEnabled: true,
            switchHtmlWallpaperScene: htmlSwitch,
            persistWallpaperScene: scenes.Add,
            miniWindow: miniWindow,
            wallpaperScene: scene,
            wallpaperFps: fps,
            miniCorner: corner,
            persistMiniCorner: corners.Add,
            refreshDisplays: refreshDisplays,
            loadAlertHttpToken: () => "test-token",
            createLocalHttpCommandServer: (_, _, _, _, _, sceneHandler) =>
            {
                sceneSwitch = sceneHandler;
                return new Server(_ => "");
            },
            timeProvider: clock);

        return new Harness
        {
            Composition = composition,
            Tray = tray!,
            Timer = timer,
            Display = display,
            Clock = clock,
            Trace = trace,
            Posted = posted,
            Imports = imports,
            PersistedScenes = scenes,
            PersistedCorners = corners,
            Platform = platform,
            SceneSwitch = sceneSwitch,
            AlertServer = server,
        };
    }

    private static void Pump(Harness harness)
    {
        // Work posted while wiring (the mini startup among it) runs in order, like the dispatcher would.
        while (harness.Posted.Count > 0)
        {
            var batch = harness.Posted.ToArray();
            harness.Posted.Clear();
            foreach (var work in batch) work();
        }
    }

    [Theory]
    [InlineData(MiniCorner.TopRight, 3152, 0)]
    [InlineData(MiniCorner.TopLeft, 0, 0)]
    [InlineData(MiniCorner.BottomLeft, 0, 1112)]
    [InlineData(MiniCorner.BottomRight, 3152, 1112)]
    public void Startup_MiniMode_ShowsTheWindowInTheConfiguredCornerOfThePrimaryWorkArea(
        MiniCorner corner, int x, int y)
    {
        var window = new FakeMiniWindow();

        using var composition = Wire(WallpaperMode.Mini, window, corner).Composition;

        var shown = Assert.Single(window.Shown);
        Assert.Equal(WallpaperScene.Raphael, shown.Scene);
        Assert.Equal(30, shown.Fps);
        Assert.Equal(new Rect(x, y, 288, 288), shown.Bounds);
    }

    [Fact]
    public void Startup_MiniMode_ShowsOnTheOwningThread_NotInlineDuringWiring()
    {
        var window = new FakeMiniWindow();

        var harness = Wire(WallpaperMode.Mini, window, queueOwningThread: true);
        using (harness.Composition)
        {
            Assert.Empty(window.Shown);

            Pump(harness);

            Assert.Single(window.Shown);
        }
    }

    [Fact]
    public void Startup_MiniMode_WhenShowFails_TracesAndKeepsRunningWithoutRetrying()
    {
        var window = new FakeMiniWindow { ShowResult = false };

        var harness = Wire(WallpaperMode.Mini, window);
        using (harness.Composition)
        {
            harness.Timer.Tick();
            harness.Timer.Tick();

            Assert.Single(window.Shown);
            Assert.Contains(harness.Trace.Lines, line =>
                line.StartsWith("mini-window", StringComparison.Ordinal)
                && line.Contains("shown=False", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Startup_MiniMode_WhenShowThrows_TracesTheTypeAndKeepsRunning()
    {
        var window = new FakeMiniWindow { ThrowOnShow = true };

        var harness = Wire(WallpaperMode.Mini, window);
        using (harness.Composition)
        {
            Assert.Contains(harness.Trace.Lines, line =>
                line.StartsWith("mini-window", StringComparison.Ordinal)
                && line.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(WallpaperMode.Video)]
    [InlineData(WallpaperMode.Html)]
    public void Startup_VideoAndHtmlModes_NeverTouchTheMiniWindow(WallpaperMode mode)
    {
        var window = new FakeMiniWindow();

        var harness = Wire(mode, window);
        using (harness.Composition)
        {
            harness.Timer.Tick();

            Assert.Empty(window.Shown);
            Assert.Empty(window.Moves);
            Assert.Empty(window.Alerts);
        }
    }

    [Fact]
    public void Startup_MiniMode_NeverAttachesTheHostNorPlaysAVideo_EvenWithAConfiguredPath()
    {
        var host = new Host();
        var player = new Player();
        var window = new FakeMiniWindow();

        var harness = Wire(WallpaperMode.Mini, window, host: host, player: player,
            videoPath: typeof(MiniModeWiringTests).Assembly.Location);
        using (harness.Composition)
        {
            harness.Timer.Tick();

            Assert.Equal(0, host.Attaches);
            Assert.Equal(0, player.Plays);
        }
    }

    [Fact]
    public void Startup_VideoMode_StillAttachesAndPlays_SoTheMiniGuardDoesNotLeakIntoIt()
    {
        var host = new Host();
        var player = new Player();

        var harness = Wire(WallpaperMode.Video, host: host, player: player,
            videoPath: typeof(MiniModeWiringTests).Assembly.Location);
        using (harness.Composition)
        {
            Assert.Equal(1, host.Attaches);
            Assert.Equal(1, player.Plays);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TrayVideoPick_MiniMode_IsSkippedLikeHtmlMode_NoImportNoPersistNoPlayback(bool withCollaborators)
    {
        var host = withCollaborators ? new Host() : null;
        var player = withCollaborators ? new Player() : null;
        var window = new FakeMiniWindow();

        var harness = Wire(WallpaperMode.Mini, window, host: host, player: player);
        using (harness.Composition)
        {
            harness.Tray.SetVideoWallpaperPath(@"C:\Users\me\Videos\clip.mp4");

            Assert.Empty(harness.Imports);
            Assert.Equal(0, host?.Attaches ?? 0);
            Assert.Equal(0, player?.Plays ?? 0);
            Assert.Contains("video-wallpaper phase=pick skipped reason=mini-mode", harness.Trace.Lines);
        }
    }

    [Fact]
    public void HttpSceneSwitch_MiniMode_AcceptsAtOnceThenSwitchesAndPersistsOnTheOwningThread()
    {
        var window = new FakeMiniWindow();
        var harness = Wire(WallpaperMode.Mini, window, queueOwningThread: true);
        using (harness.Composition)
        {
            Pump(harness);
            Assert.NotNull(harness.SceneSwitch);

            var accepted = harness.SceneSwitch!("idle");

            Assert.True(accepted);
            Assert.Empty(window.Switched);
            Assert.Empty(harness.PersistedScenes);

            Assert.Single(harness.Posted)();

            Assert.Equal([WallpaperScene.Idle], window.Switched);
            Assert.Equal([WallpaperScene.Idle], harness.PersistedScenes);
        }
    }

    [Fact]
    public void HttpSceneSwitch_MiniMode_WhenTheWindowRefuses_DoesNotPersist()
    {
        var window = new FakeMiniWindow { SwitchResult = false };
        var harness = Wire(WallpaperMode.Mini, window);
        using (harness.Composition)
        {
            Assert.True(harness.SceneSwitch!("explorer"));

            Assert.Equal([WallpaperScene.Explorer], window.Switched);
            Assert.Empty(harness.PersistedScenes);
        }
    }

    [Fact]
    public void HttpSceneSwitch_MiniModeWithNoWindow_ReturnsFalse()
    {
        var harness = Wire(WallpaperMode.Mini, miniWindow: null);
        using (harness.Composition)
        {
            Assert.False(harness.SceneSwitch!("idle"));
            Assert.Empty(harness.PersistedScenes);
        }
    }

    [Fact]
    public void HttpSceneSwitch_VideoMode_StillReturnsFalseEvenWithAMiniWindowWired()
    {
        var window = new FakeMiniWindow();
        var harness = Wire(WallpaperMode.Video, window);
        using (harness.Composition)
        {
            Assert.False(harness.SceneSwitch!("idle"));
            Assert.Empty(window.Switched);
        }
    }

    [Fact]
    public void HttpSceneSwitch_HtmlMode_StillUsesTheAlertLayerSwitchNotTheMiniWindow()
    {
        var window = new FakeMiniWindow();
        WallpaperScene? htmlSwitched = null;
        var harness = Wire(WallpaperMode.Html, window, htmlSwitch: scene => { htmlSwitched = scene; return true; });
        using (harness.Composition)
        {
            Assert.True(harness.SceneSwitch!("idle"));

            Assert.Equal(WallpaperScene.Idle, htmlSwitched);
            Assert.Empty(window.Switched);
            Assert.Equal([WallpaperScene.Idle], harness.PersistedScenes);
        }
    }

    [Fact]
    public void Alerts_MiniMode_AreRoutedToTheMiniWindowAndHiddenWhenTheyExpire()
    {
        var window = new FakeMiniWindow();
        var harness = Wire(WallpaperMode.Mini, window, alerts: true);
        using (harness.Composition)
        {
            Assert.Equal(AlertPipeProtocol.OkReply, harness.AlertServer!.Send("warning:1 duration:1"));

            var request = Assert.Single(window.Alerts);
            Assert.Equal(["warning"], request.Tiles);
            Assert.Equal(0, window.Hides);

            harness.Clock.Advance(TimeSpan.FromSeconds(2));
            harness.Timer.Tick();

            Assert.Equal(1, window.Hides);
        }
    }

    [Fact]
    public void Alerts_MiniMode_WhenShowFailed_AreHeldAndNeverSentToTheWindow()
    {
        // A window that never came up must not be told to show alerts it cannot draw: the queue keeps
        // them pending (desktop not visible), exactly as html mode does before its host attaches.
        var window = new FakeMiniWindow { ShowResult = false };
        var harness = Wire(WallpaperMode.Mini, window, alerts: true);
        using (harness.Composition)
        {
            harness.AlertServer!.Send("warning:1 duration:1");

            Assert.Empty(window.Alerts);
        }
    }

    [Fact]
    public void Alerts_VideoMode_NeverReachTheMiniWindow()
    {
        var window = new FakeMiniWindow();
        var harness = Wire(WallpaperMode.Video, window, alerts: true);
        using (harness.Composition)
        {
            harness.AlertServer!.Send("warning:1 duration:1");
            harness.Timer.Tick();

            Assert.Empty(window.Alerts);
        }
    }

    [Fact]
    public void DisplayChange_MiniMode_ReplacesTheWindowOnTheNewWorkArea()
    {
        var window = new FakeMiniWindow();
        var changes = new Queue<IReadOnlyList<IDisplay>>();
        var harness = Wire(WallpaperMode.Mini, window, MiniCorner.BottomRight,
            refreshDisplays: () => changes.Count > 0 ? changes.Dequeue() : []);
        using (harness.Composition)
        {
            // The taskbar grows to 100px.
            harness.Display.WorkArea = Rectangle.FromSize(0, 0, 3440, 1340);
            changes.Enqueue([harness.Display]);

            harness.Timer.Tick();

            Assert.Equal([new Rect(3152, 1052, 288, 288)], window.Moves);
        }
    }

    [Fact]
    public void DisplayChange_VideoMode_NeverMovesTheMiniWindow()
    {
        var window = new FakeMiniWindow();
        var changes = new Queue<IReadOnlyList<IDisplay>>();
        var harness = Wire(WallpaperMode.Video, window,
            refreshDisplays: () => changes.Count > 0 ? changes.Dequeue() : []);
        using (harness.Composition)
        {
            harness.Display.WorkArea = Rectangle.FromSize(0, 0, 3440, 1340);
            changes.Enqueue([harness.Display]);

            harness.Timer.Tick();

            Assert.Empty(window.Moves);
        }
    }

    [Fact]
    public void Dispose_MiniMode_DisposesTheWindow()
    {
        var window = new FakeMiniWindow();
        var harness = Wire(WallpaperMode.Mini, window);

        harness.Composition.Dispose();

        Assert.Equal(1, window.Disposed);
    }

    private static async Task<bool> WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        return condition();
    }

    private static async Task PressAltM(Harness harness, int expectedTotalMoves, FakeMiniWindow window)
    {
        Assert.True(harness.Platform.Raise(KeyboardKey.M, isKeyDown: true, ModifierKeys.Alt));
        Assert.True(await WaitUntil(() => window.Moves.Count == expectedTotalMoves));
    }

    /// <summary>
    /// T5: Alt+M walks the corners clockwise from the configured one, moving the window onto the
    /// computed rectangle and persisting each new corner -- driven through the real hook, with tiling
    /// still ON (the chord does not depend on it; the executor test covers tiling off).
    /// </summary>
    [Fact]
    public async Task AltM_MiniMode_CyclesTopRightBottomRightBottomLeftTopLeftAndPersistsEachStep()
    {
        var window = new FakeMiniWindow();
        var harness = Wire(WallpaperMode.Mini, window, MiniCorner.TopRight);
        using (harness.Composition)
        {
            await PressAltM(harness, 1, window);
            await PressAltM(harness, 2, window);
            await PressAltM(harness, 3, window);
            await PressAltM(harness, 4, window);

            Assert.Equal(
                [new Rect(3152, 1112, 288, 288), new Rect(0, 1112, 288, 288),
                 new Rect(0, 0, 288, 288), new Rect(3152, 0, 288, 288)],
                window.Moves);
            Assert.Equal(
                [MiniCorner.BottomRight, MiniCorner.BottomLeft, MiniCorner.TopLeft, MiniCorner.TopRight],
                harness.PersistedCorners);
        }
    }

    [Fact]
    public async Task AltM_MiniMode_PlacesOnTheWorkAreaThatIsTrueNow_NotTheOneSeenAtStartup()
    {
        var window = new FakeMiniWindow();
        var harness = Wire(WallpaperMode.Mini, window, MiniCorner.TopRight);
        using (harness.Composition)
        {
            harness.Display.WorkArea = Rectangle.FromSize(0, 0, 3440, 1340);

            await PressAltM(harness, 1, window);

            Assert.Equal([new Rect(3152, 1052, 288, 288)], window.Moves);
        }
    }

    [Fact]
    public async Task AltM_MiniMode_RunsTheMoveOnTheOwningThread()
    {
        var window = new FakeMiniWindow();
        var harness = Wire(WallpaperMode.Mini, window, queueOwningThread: true);
        using (harness.Composition)
        {
            Pump(harness);
            Assert.True(harness.Platform.Raise(KeyboardKey.M, isKeyDown: true, ModifierKeys.Alt));
            Assert.True(await WaitUntil(() => harness.Posted.Count > 0));
            Assert.Empty(window.Moves);

            Pump(harness);

            Assert.Single(window.Moves);
            Assert.Equal([MiniCorner.BottomRight], harness.PersistedCorners);
        }
    }

    [Theory]
    [InlineData(WallpaperMode.Video)]
    [InlineData(WallpaperMode.Html)]
    public async Task AltM_OutsideMiniMode_IsANoOpWithATrace(WallpaperMode mode)
    {
        var window = new FakeMiniWindow();
        var harness = Wire(mode, window);
        using (harness.Composition)
        {
            Assert.True(harness.Platform.Raise(KeyboardKey.M, isKeyDown: true, ModifierKeys.Alt));
            Assert.True(await WaitUntil(() => harness.Trace.Lines.Contains("mini-corner cycle skipped reason=not-mini-mode")));

            Assert.Empty(window.Moves);
            Assert.Empty(harness.PersistedCorners);
        }
    }

    [Fact]
    public async Task AltM_MiniModeWithNoWindow_IsANoOp()
    {
        var harness = Wire(WallpaperMode.Mini, miniWindow: null);
        using (harness.Composition)
        {
            Assert.True(harness.Platform.Raise(KeyboardKey.M, isKeyDown: true, ModifierKeys.Alt));
            await Task.Delay(100);

            Assert.Empty(harness.PersistedCorners);
        }
    }

    [Fact]
    public void WireProduction_InMiniMode_BuildsTheMiniWindowAndNoWallpaperHostPlayerOrAlertLayer()
    {
        var source = ReadAppCompositionSource();
        var start = source.IndexOf("public static AppComposition WireProduction(", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var body = source[start..];

        Assert.Contains("MiniSceneWindowController.CreateProduction(", body);
        Assert.Contains("settings.WallpaperMode == WallpaperMode.Mini", body);
        // The host, the player, the alert layer and the video thread exist only outside mini mode.
        Assert.Matches(@"videoWallpaperHost\s*=\s*miniMode\s*\?\s*null", body);
        Assert.Matches(@"videoWallpaperPlayer\s*=\s*miniMode\s*\?\s*null", body);
        Assert.Matches(@"alertLayer\s*=\s*settings\.AlertsEnabled\s*&&\s*!miniMode", body);
        Assert.Matches(@"videoWallpaperThread\s*=\s*miniMode\s*\?\s*null", body);
    }

    private static string ReadAppCompositionSource([CallerFilePath] string testFilePath = "")
    {
        var dir = Path.GetDirectoryName(testFilePath)!;
        return File.ReadAllText(Path.GetFullPath(Path.Combine(dir, "..", "..", "CosmicWin.App", "AppComposition.cs")));
    }
}
