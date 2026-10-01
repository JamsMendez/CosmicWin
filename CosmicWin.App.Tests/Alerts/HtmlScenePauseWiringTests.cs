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
/// pause-scene-when-covered T2: in html wallpaper mode the composition polls the SAME covered-desktop
/// signal that holds alerts (<c>isPrimaryMonitorCovered</c>) on the existing watch tick and tells the
/// scene page to pause while it is covered and to resume when it is not. Only a CHANGE is sent (and
/// traced); video mode, mini mode and a host that never attached are never paused.
/// </summary>
public sealed class HtmlScenePauseWiringTests
{
    private const string PausedLine = "wallpaper-scene paused: desktop covered";
    private const string ResumedLine = "wallpaper-scene resumed";

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

    private sealed class Disposable : IDisposable { public void Dispose() { } }

    private sealed class Foreground : IForegroundWindowSource { public nint GetForegroundHandle() => 0; }

    private sealed class RecordingDesktopTrace : CosmicWin.App.Diagnostics.IDesktopTrace
    {
        public List<string> Lines { get; } = [];

        public void Record(string line) => Lines.Add(line);
    }

    private sealed class Host : IVideoWallpaperHost
    {
        public bool FailAttach { get; set; }
        public bool TryAttach() => !FailAttach;
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
        public bool TryPlay(IVideoWallpaperHost host, string path) => true;
        public void Stop() { }
        public void Dispose() { }
    }

    private sealed record Harness(
        AppComposition Composition, Scheduler Timer, List<string> PauseCalls, RecordingDesktopTrace Trace);

    private static Harness Create(
        Func<bool> covered, WallpaperMode wallpaperMode = WallpaperMode.Html, Host? host = null)
    {
        var timer = new Scheduler();
        var trace = new RecordingDesktopTrace();
        var pauseCalls = new List<string>();
        var display = new FakeDisplay((nint)1, Rectangle.FromSize(0, 0, 1920, 1080),
            Rectangle.FromSize(0, 0, 1920, 1080), 1.0, true);
        var registry = new WindowRegistry();
        var composition = AppComposition.Wire(new FakeWorkspace(),
            new TreeManager([display], display, registry), registry,
            new Foreground(), new ExceptionListStore(ExceptionList.Empty), new RecordingFocusTrace(),
            () => { }, timer.Schedule,
            writer => new LowLevelKeyboardHook(writer, new FakeKeyboardHookPlatform(), TimeSpan.FromSeconds(5), () => 0),
            () => ExceptionList.Empty, () => { }, _ => new Disposable(), path => path,
            desktopTrace: trace,
            alertsEnabled: true,
            createAlertCommandServer: (_, handle, _) => new NoServer(),
            isPrimaryMonitorCovered: covered,
            videoWallpaperHost: host ?? new Host(),
            videoWallpaperPlayer: new Player(),
            videoWallpaperPath: typeof(HtmlScenePauseWiringTests).Assembly.Location,
            scheduleVideoWallpaperWork: work => work(),
            startAlertLayer: _ => { },
            endAlertLayer: () => { },
            wallpaperMode: wallpaperMode,
            setHtmlWallpaperScenePaused: paused => pauseCalls.Add(paused ? "pause" : "resume"));
        return new Harness(composition, timer, pauseCalls, trace);
    }

    private sealed class NoServer : IAlertCommandServer
    {
        public void Start() { }
        public void Dispose() { }
        public string Send(string command) => string.Empty;
    }

    [Fact]
    public void ACoveredDesktop_PausesTheScene_OnceAndTracesIt()
    {
        var h = Create(() => true);
        using (h.Composition)
        {
            h.Timer.Tick();
            h.Timer.Tick();
            h.Timer.Tick();

            Assert.Equal(["pause"], h.PauseCalls);
            Assert.Single(h.Trace.Lines, line => line == PausedLine);
        }
    }

    [Fact]
    public void UncoveringTheDesktop_ResumesTheScene_OnceAndTracesIt()
    {
        var covered = true;
        var h = Create(() => covered);
        using (h.Composition)
        {
            h.Timer.Tick();
            covered = false;
            h.Timer.Tick();
            h.Timer.Tick();

            Assert.Equal(["pause", "resume"], h.PauseCalls);
            Assert.Single(h.Trace.Lines, line => line == ResumedLine);
        }
    }

    [Fact]
    public void ADesktopThatWasNeverCovered_SendsNothingAndTracesNothing()
    {
        var h = Create(() => false);
        using (h.Composition)
        {
            h.Timer.Tick();
            h.Timer.Tick();

            Assert.Empty(h.PauseCalls);
            Assert.DoesNotContain(h.Trace.Lines, line => line.StartsWith("wallpaper-scene", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ASceneCanBePausedAgainAfterItResumed()
    {
        var covered = true;
        var h = Create(() => covered);
        using (h.Composition)
        {
            h.Timer.Tick();
            covered = false;
            h.Timer.Tick();
            covered = true;
            h.Timer.Tick();

            Assert.Equal(["pause", "resume", "pause"], h.PauseCalls);
        }
    }

    [Fact]
    public void InVideoMode_ACoveredDesktopNeverPausesTheScene()
    {
        var h = Create(() => true, wallpaperMode: WallpaperMode.Video);
        using (h.Composition)
        {
            h.Timer.Tick();

            Assert.Empty(h.PauseCalls);
            Assert.DoesNotContain(h.Trace.Lines, line => line.StartsWith("wallpaper-scene", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void InMiniMode_ACoveredDesktopNeverPausesTheScene()
    {
        var h = Create(() => true, wallpaperMode: WallpaperMode.HtmlMini);
        using (h.Composition)
        {
            h.Timer.Tick();

            Assert.Empty(h.PauseCalls);
            Assert.DoesNotContain(h.Trace.Lines, line => line.StartsWith("wallpaper-scene", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void WhenTheHostNeverAttaches_ACoveredDesktopSendsNothing()
    {
        var h = Create(() => true, host: new Host { FailAttach = true });
        using (h.Composition)
        {
            h.Timer.Tick();

            Assert.Empty(h.PauseCalls);
        }
    }
}
