using CosmicWin.App.Alerts;
using CosmicWin.App.Input;
using CosmicWin.Interop;
using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.App.Tray;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;
using Windows.Win32.Graphics.Direct3D11;

namespace CosmicWin.App.Tests.Alerts;

public sealed class WebViewAlertCompositionWiringTests
{
    private sealed class Scheduler
    {
        private Action? _tick;
        public IDisposable Schedule(TimeSpan interval, Action tick)
        {
            Assert.Equal(TimeSpan.FromMilliseconds(400), interval);
            _tick = tick;
            return new Disposable();
        }
        public void Tick() => _tick!();
    }

    private sealed class Disposable : IDisposable { public void Dispose() { } }
    private sealed class Foreground : IForegroundWindowSource { public nint GetForegroundHandle() => 0; }
    private sealed class Server(Func<string, string> handle) : IAlertCommandServer
    {
        public void Start() { }
        public void Dispose() { }
        public string Send(string command) => handle(command);
    }

    private sealed class Host : IVideoWallpaperHost
    {
        public int Attempts { get; private set; }
        public bool TryAttach() => ++Attempts != 2;
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

    private static (AppComposition Composition, Scheduler Timer, Server Server, List<string> Events) Create(
        Func<bool>? visible = null, bool enabled = true, Func<bool>? ready = null,
        Host? host = null)
    {
        var events = new List<string>();
        var timer = new Scheduler();
        Server? server = null;
        var display = new FakeDisplay((nint)1, Rectangle.FromSize(0, 0, 1920, 1080),
            Rectangle.FromSize(0, 0, 1920, 1080), 1.0, true);
        var composition = AppComposition.Wire(new FakeWorkspace(),
            new TreeManager([display], display, new WindowRegistry()), new WindowRegistry(),
            new Foreground(), new ExceptionListStore(ExceptionList.Empty), new RecordingFocusTrace(),
            () => { }, timer.Schedule,
            writer => new LowLevelKeyboardHook(writer, new FakeKeyboardHookPlatform(), TimeSpan.FromSeconds(5), () => 0),
            () => ExceptionList.Empty, () => { }, _ => new Disposable(), path => path,
            alertsEnabled: enabled,
            createAlertCommandServer: (_, handle, _) => server = new Server(handle),
            alertDesktopVisible: host is null ? visible ?? (() => true) : null,
            videoWallpaperHost: host,
            videoWallpaperPlayer: host is null ? null : new Player(),
            videoWallpaperPath: host is null ? null : typeof(WebViewAlertCompositionWiringTests).Assembly.Location,
            scheduleVideoWallpaperWork: work => work(),
            alertRendererReady: ready,
            startAlertLayer: (kind, duration) => events.Add($"start:{kind}:{duration}"),
            endAlertLayer: () => events.Add("end"),
            shakeAlertVideo: duration => events.Add($"shake:{duration.TotalMilliseconds}"));
        return (composition, timer, server!, events);
    }

    [Fact]
    public void CombinedCommand_StartsFailedOnceAndEndsAfterDuration()
    {
        var h = Create();
        using (h.Composition)
        {
            Assert.Equal(AlertPipeProtocol.OkReply, h.Server.Send("warning:2 failed:1 duration:1"));
            h.Timer.Tick();
            h.Timer.Tick();
            Assert.Equal("shake:120", h.Events[0]);
            Assert.StartsWith("start:failed:", h.Events[1]);
            Assert.InRange(int.Parse(h.Events[1]["start:failed:".Length..]), 1, 1000);
            Thread.Sleep(1100);
            h.Timer.Tick();
            Assert.Equal("end", h.Events.Last());
        }
    }

    [Fact]
    public void CoveredQueueWaitsAndNextAlertStartsAfterPreviousEnds()
    {
        var visible = false;
        var h = Create(() => visible);
        using (h.Composition)
        {
            Assert.Equal(AlertPipeProtocol.OkReply, h.Server.Send("warning:1 duration:1"));
            Assert.Equal(AlertPipeProtocol.OkReply, h.Server.Send("failed:1 duration:1"));
            h.Timer.Tick();
            Assert.Empty(h.Events);
            visible = true;
            h.Timer.Tick();
            Assert.StartsWith("start:warning:", Assert.Single(h.Events));
            Thread.Sleep(1100);
            h.Timer.Tick();
            Assert.Equal(4, h.Events.Count);
            Assert.StartsWith("start:warning:", h.Events[0]);
            Assert.Equal("end", h.Events[1]);
            Assert.Equal("shake:120", h.Events[2]);
            Assert.StartsWith("start:failed:", h.Events[3]);
            Assert.InRange(int.Parse(h.Events[3]["start:failed:".Length..]), 1, 1000);
        }
    }

    [Fact]
    public void UnavailableRendererHoldsPendingButNotAlreadyActiveAlert()
    {
        var ready = false;
        var h = Create(ready: () => ready);
        using (h.Composition)
        {
            Assert.Equal(AlertPipeProtocol.OkReply, h.Server.Send("warning:1 duration:1"));
            h.Timer.Tick();
            Assert.Empty(h.Events);
            ready = true;
            h.Timer.Tick();
            Assert.StartsWith("start:warning:", Assert.Single(h.Events));
            ready = false;
            Thread.Sleep(1100);
            h.Timer.Tick();
            Assert.Equal("end", h.Events.Last());
        }
    }

    [Fact]
    public void FailedKeepAliveRetriesAndQueuedAlertWaitsForRenderer()
    {
        var host = new Host();
        var ready = false;
        var h = Create(ready: () => ready, host: host);
        using (h.Composition)
        {
            Assert.Equal(1, host.Attempts); // Startup activation.
            Assert.Equal(AlertPipeProtocol.OkReply, h.Server.Send("warning:1 duration:1"));
            h.Timer.Tick(); // Transient keep-alive failure.
            Assert.Equal(2, host.Attempts);
            Assert.Empty(h.Events);
            h.Timer.Tick(); // Next 400ms watch tick must retry, even while renderer is unready.
            Assert.Equal(3, host.Attempts);
            Assert.Empty(h.Events);
            ready = true;
            h.Timer.Tick();
            Assert.StartsWith("start:warning:", Assert.Single(h.Events));
        }
    }

    [Fact]
    public void DisabledSettingNeverStartsLayer()
    {
        var h = Create(enabled: false);
        using (h.Composition) h.Timer.Tick();
        Assert.Empty(h.Events);
    }
}
