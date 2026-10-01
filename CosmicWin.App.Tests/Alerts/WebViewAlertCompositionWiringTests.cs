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
    /// <summary>
    /// Manual fake for the alert queue's own clock (<c>AppComposition.Wire</c>'s
    /// <c>timeProvider</c> seam) -- there is no <c>Microsoft.Extensions.TimeProvider.Testing</c>
    /// reference in this test project, so this mirrors the same manual-subclass pattern
    /// <c>CosmicWin.Interop.Tests</c> already uses for its own shake timing tests. Lets these tests
    /// advance past a queue's second-scale duration deterministically instead of via
    /// <c>Thread.Sleep</c> against the real clock.
    /// </summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public FakeTimeProvider(DateTimeOffset start) => _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

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

    private sealed class RecordingDesktopTrace : CosmicWin.App.Diagnostics.IDesktopTrace
    {
        public List<string> Lines { get; } = [];

        public void Record(string line) => Lines.Add(line);
    }

    private sealed class Host : IVideoWallpaperHost
    {
        public int Attempts { get; private set; }
        public bool FailAttach { get; set; }
        public bool TryAttach() { Attempts++; return !FailAttach && Attempts != 2; }
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

    /// <summary>
    /// alert-tile-mosaic (2026-09-26): every fixture in this file used to encode a single-kind
    /// contract (<c>Action&lt;string, int&gt;</c>, "start:{kind}:{duration}") -- rewritten, not
    /// silently dropped, to the new <see cref="AlertShowRequest"/> contract. The recorded event
    /// string keeps the SAME "start:" prefix and kind-name convention every existing assertion below
    /// already greps for (<c>StartsWith("start:warning:", ...)</c> etc.), with the grid/gap appended,
    /// so only the assertions that actually care about grid/gap needed new text.
    /// <para>
    /// T7: the work area segment ("work=L,T,WxH") is inserted between <c>gap=</c> and the trailing
    /// duration, using commas (never a colon) inside it -- every existing assertion below either
    /// checks a PREFIX ending at "gap=...:" (unaffected) or parses the duration from the string's
    /// LAST colon (<c>LastIndexOf(':')</c>), which still lands correctly since nothing after the work
    /// segment contains another colon.
    /// </para>
    /// </summary>
    private static string DescribeShow(AlertShowRequest request) =>
        $"start:{string.Join(",", request.Tiles)}:{request.Columns}x{request.Rows}:gap={request.Gap}:" +
        $"work={request.WorkAreaLeft},{request.WorkAreaTop},{request.WorkAreaWidth}x{request.WorkAreaHeight}:" +
        $"{request.DurationMilliseconds}";

    /// <summary>
    /// T10 (alert-tile-mosaic, review follow-up R3-workarea-catch-path-unexercised): a display whose
    /// <see cref="WorkArea"/> getter throws, proving <c>UpdateAlertOverlay</c>'s try/catch around
    /// <c>AlertLayerWorkArea.Resolve</c> actually degrades to
    /// <see cref="AlertLayerWorkArea.Unavailable"/> and traces <c>alert-layer-workarea-failed</c>,
    /// instead of being an untested catch block that could silently stop catching anything.
    /// </summary>
    /// <remarks>
    /// Throws from the SECOND read onward, not the first: <c>AppComposition.Wire</c> itself already
    /// reads the primary display's <c>WorkArea</c> once, eagerly, at wiring time
    /// (<c>WorkAreaResolver.Resolve</c> on the primary display, for the initial tiling
    /// layout) -- a display that throws unconditionally breaks composition before an alert is ever
    /// sent, proving nothing about the alert's OWN try/catch. <see cref="Bounds"/> stays an ordinary
    /// rect throughout: <c>AlertLayerWorkArea.Resolve</c>'s caller reads it as the surface argument
    /// alongside <see cref="WorkArea"/>, and only the work-area read needs to fail here.
    /// </remarks>
    private sealed class ThrowingWorkAreaDisplay : IDisplay
    {
        private int _reads;

        public IntPtr Handle => (IntPtr)2;
        public Rectangle Bounds { get; } = Rectangle.FromSize(0, 0, 1920, 1080);

        public Rectangle WorkArea =>
            ++_reads == 1
                ? Bounds
                : throw new InvalidOperationException("work area unavailable");

        public double Scaling => 1.0;
        public bool IsPrimary => true;
        public bool Equals(IDisplay? other) => other is not null && Handle == other.Handle;
        public override bool Equals(object? obj) => obj is IDisplay other && Equals(other);
        public override int GetHashCode() => Handle.GetHashCode();
    }

    private static (AppComposition Composition, Scheduler Timer, Server Server, List<string> Events, FakeTimeProvider Clock, RecordingDesktopTrace Trace, FakeDisplay Display) Create(
        Func<bool>? visible = null, bool enabled = true, Func<bool>? ready = null,
        Host? host = null, Action<AlertShowRequest>? startAlertLayer = null, Action? preloadAlertLayer = null,
        IDisplay? primaryDisplay = null, WallpaperMode wallpaperMode = WallpaperMode.Video)
    {
        var events = new List<string>();
        var timer = new Scheduler();
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var trace = new RecordingDesktopTrace();
        Server? server = null;
        var display = new FakeDisplay((nint)1, Rectangle.FromSize(0, 0, 1920, 1080),
            Rectangle.FromSize(0, 0, 1920, 1080), 1.0, true);
        // T10: swaps in a throwing display for the wired primary while the tuple's own `Display`
        // field keeps returning the safe FakeDisplay above -- no existing fixture that mutates
        // `h.Display.WorkArea` after wiring is affected, since none of them pass primaryDisplay.
        var effectivePrimary = primaryDisplay ?? display;
        var composition = AppComposition.Wire(new FakeWorkspace(),
            new TreeManager([effectivePrimary], effectivePrimary, new WindowRegistry()), new WindowRegistry(),
            new Foreground(), new ExceptionListStore(ExceptionList.Empty), new RecordingFocusTrace(),
            () => { }, timer.Schedule,
            writer => new LowLevelKeyboardHook(writer, new FakeKeyboardHookPlatform(), TimeSpan.FromSeconds(5), () => 0),
            () => ExceptionList.Empty, () => { }, _ => new Disposable(), path => path,
            desktopTrace: trace,
            alertsEnabled: enabled,
            createAlertCommandServer: (_, handle, _) => server = new Server(handle),
            alertDesktopVisible: host is null ? visible ?? (() => true) : null,
            videoWallpaperHost: host,
            videoWallpaperPlayer: host is null ? null : new Player(),
            videoWallpaperPath: host is null ? null : typeof(WebViewAlertCompositionWiringTests).Assembly.Location,
            scheduleVideoWallpaperWork: work => work(),
            alertRendererReady: ready,
            startAlertLayer: startAlertLayer ?? (request => events.Add(DescribeShow(request))),
            endAlertLayer: () => events.Add("end"),
            shakeAlertVideo: duration => events.Add($"shake:{duration.TotalMilliseconds}"),
            preloadAlertLayer: preloadAlertLayer,
            wallpaperMode: wallpaperMode,
            timeProvider: clock);
        return (composition, timer, server!, events, clock, trace, display);
    }

    /// <summary>
    /// T9c (webview-alert-layer): the preloaded controller must be created once, at startup, when
    /// alerts are enabled -- not lazily on the first alert (the whole point of the persistent-preload
    /// fix for T6's F1/F2). <c>onOwningThread</c> runs synchronously in these tests (no
    /// <c>scheduleOnOwningThread</c> supplied), so this is exercised without a real dispatcher.
    /// </summary>
    [Fact]
    public void PreloadAlertLayerRunsOnceDuringWiringWhenAlertsAreEnabled()
    {
        var calls = 0;
        var h = Create(preloadAlertLayer: () => calls++);
        using (h.Composition) Assert.Equal(1, calls);
    }

    [Fact]
    public void PreloadAlertLayerNeverRunsWhenAlertsAreDisabled()
    {
        var calls = 0;
        var h = Create(enabled: false, preloadAlertLayer: () => calls++);
        using (h.Composition) Assert.Equal(0, calls);
    }

    /// <summary>
    /// T9d (webview-alert-layer): a newly accepted command must start the layer as soon as it is
    /// queued, not wait up to the 400ms watch tick -- this test never calls <c>h.Timer.Tick()</c> at
    /// all, so <c>h.Events</c> can only be non-empty if enqueueing itself scheduled the update.
    /// </summary>
    [Fact]
    public void EnqueuedAlertStartsTheLayerImmediatelyWithoutAdvancingTheWatchTick()
    {
        var h = Create();
        using (h.Composition)
        {
            Assert.Equal(AlertPipeProtocol.OkReply, h.Server.Send("warning:1 duration:1"));
            Assert.StartsWith("start:warning:", Assert.Single(h.Events));
        }
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
            // 1 failed + 2 warning = 3 tiles -> grid 2x2 (feature doc, decision 2), failed first.
            Assert.StartsWith("start:failed,warning,warning:2x2:gap=", h.Events[1]);
            Assert.InRange(int.Parse(h.Events[1][(h.Events[1].LastIndexOf(':') + 1)..]), 1, 1000);
            h.Clock.Advance(TimeSpan.FromMilliseconds(1100));
            h.Timer.Tick();
            Assert.Equal("end", h.Events.Last());
        }
    }

    /// <summary>
    /// Rewritten for alert-busy-ignore (maintainer decision, 2026-09-26): this used to prove TWO
    /// alerts queued while covered would both eventually show, one after another -- exactly the
    /// stacking the new busy-ignore rule forbids. The second command sent here now finds the first
    /// alert already WAITING (queued while covered, not yet shown) and is ignored instead of queued:
    /// it must still answer ok, the trace must record the ignore, and only the first alert is ever
    /// shown, even after it ends.
    /// </summary>
    [Fact]
    public void ASecondCommandWhileCoveredAndWaiting_IsIgnored_AndOnlyTheFirstEverShows()
    {
        var visible = false;
        var h = Create(() => visible);
        using (h.Composition)
        {
            Assert.Equal(AlertPipeProtocol.OkReply, h.Server.Send("warning:1 duration:1"));
            Assert.Equal(AlertPipeProtocol.OkReply, h.Server.Send("failed:1 duration:1"));
            Assert.Contains(h.Trace.Lines, line => line.Contains("alert ignored", StringComparison.Ordinal));

            h.Timer.Tick();
            Assert.Empty(h.Events);

            visible = true;
            h.Timer.Tick();
            Assert.StartsWith("start:warning:", Assert.Single(h.Events));

            h.Clock.Advance(TimeSpan.FromMilliseconds(1100));
            h.Timer.Tick();
            Assert.Equal(2, h.Events.Count);
            Assert.StartsWith("start:warning:", h.Events[0]);
            Assert.Equal("end", h.Events[1]);
        }
    }

    /// <summary>
    /// A2 (alert-busy-ignore): the same <c>HandleAlertCommand</c> entry both the pipe and the HTTP
    /// route reach through -- proven here via the pipe's <c>Server.Send</c>, see the feature's task
    /// file for why no separate HTTP-level test is needed -- must answer ok for a second command
    /// while the first is still SHOWING, exactly as for a queued one, and the desktop trace must
    /// record the ignore. Only the first alert's start event is ever seen.
    /// </summary>
    [Fact]
    public void ASecondCommandWhileShowing_StillRepliesOk_ButIsIgnored()
    {
        var h = Create();
        using (h.Composition)
        {
            Assert.Equal(AlertPipeProtocol.OkReply, h.Server.Send("warning:1 duration:1"));
            Assert.StartsWith("start:warning:", Assert.Single(h.Events));

            Assert.Equal(AlertPipeProtocol.OkReply, h.Server.Send("failed:1 duration:1"));
            Assert.Contains(h.Trace.Lines, line => line.Contains("alert ignored", StringComparison.Ordinal));
            Assert.DoesNotContain(h.Events, e => e.StartsWith("start:failed:", StringComparison.Ordinal));

            h.Clock.Advance(TimeSpan.FromMilliseconds(1100));
            h.Timer.Tick();

            Assert.DoesNotContain(h.Events, e => e.StartsWith("start:failed:", StringComparison.Ordinal));
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
            h.Clock.Advance(TimeSpan.FromMilliseconds(1100));
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
    public void FailedAlertLayerStartDoesNotEscapeTheTickAndIsRetriedNextTick()
    {
        var attempts = 0;
        List<string>? events = null;
        var h = Create(startAlertLayer: request =>
        {
            attempts++;
            events!.Add(DescribeShow(request));
            if (attempts == 1) throw new InvalidOperationException("start failed");
        });
        events = h.Events;
        using (h.Composition)
        {
            // T9d: enqueuing itself already ticks the overlay once -- the first (failing) attempt
            // happens here, not on the first explicit watch tick.
            string? reply = null;
            var thrown = Record.Exception(() => reply = h.Server.Send("failed:1 duration:1"));
            Assert.Null(thrown);
            Assert.Equal(AlertPipeProtocol.OkReply, reply);
            Assert.Equal(1, attempts);
            Assert.StartsWith("start:failed:", h.Events[1]);

            // Retried on the next tick because the failed start must not have recorded the
            // alert as displayed.
            thrown = Record.Exception(() => h.Timer.Tick());
            Assert.Null(thrown);
            Assert.Equal(2, attempts);
            Assert.StartsWith("start:failed:", h.Events[2]);

            // The failed-kind shake happens once per alert, not once per retry attempt.
            Assert.Single(h.Events, e => e == "shake:120");
        }
    }

    /// <summary>
    /// alert-tile-mosaic (2026-09-26): the composition layer must thread the FULL ordered tile list
    /// and grid <see cref="AlertTileLayout"/> computes from a multi-group command, plus <see
    /// cref="TreeArranger.Gap"/> read at show time -- not just a single collapsed "failed"/"warning"
    /// kind, which is all this composition used to pass before this feature. <see
    /// cref="TreeArranger.Gap"/> is a shared mutable static (see its own remarks on why that is
    /// normally risky); mutating it here is safe only because the whole CosmicWin.App.Tests assembly
    /// disables test parallelization (<c>TestParallelism.cs</c>), the same reasoning that file
    /// documents -- the original value is restored in <c>finally</c> so no other test is affected.
    /// </summary>
    [Fact]
    public void MultiGroupCommand_ThreadsTheFullTileListGridAndGapToTheLayer()
    {
        var originalGap = TreeArranger.Gap;
        TreeArranger.Gap = 12;
        try
        {
            var h = Create();
            using (h.Composition)
            {
                Assert.Equal(AlertPipeProtocol.OkReply, h.Server.Send("failed:3 warning:2 duration:1"));
                // A failed tile is present, so the video shakes once, ahead of the start event.
                Assert.Equal("shake:120", h.Events[0]);
                // 3 failed + 2 warning = 5 tiles -> grid 3x2 (feature doc, decision 2), failed first.
                Assert.StartsWith("start:failed,failed,failed,warning,warning:3x2:gap=12:", h.Events[1]);
            }
        }
        finally
        {
            TreeArranger.Gap = originalGap;
        }
    }

    /// <summary>
    /// Review finding R3-negative-gap-blocks-alert: <see cref="WebViewAlertLayerController.Start"/>
    /// throws <see cref="ArgumentOutOfRangeException"/> on a negative <see
    /// cref="AlertShowRequest.Gap"/> -- and <c>TreeArranger.Gap</c> is a shared mutable static
    /// nothing stops another caller from setting negative, T5's new <c>gap</c> settings key rejects
    /// anything outside 0-64 but that guard lives in <c>Settings.Parse</c>, not on the static field
    /// itself. <c>AppComposition.UpdateAlertOverlay</c> must clamp at the read site, so a negative
    /// <c>TreeArranger.Gap</c> costs the alert nothing instead of taking the whole show down with an
    /// unhandled exception on the reconciliation tick.
    /// </summary>
    [Fact]
    public void ANegativeTreeArrangerGap_IsClampedToZeroRatherThanThrowing()
    {
        var originalGap = TreeArranger.Gap;
        TreeArranger.Gap = -5;
        try
        {
            var h = Create();
            using (h.Composition)
            {
                var thrown = Record.Exception(
                    () => h.Server.Send("warning:1 duration:1"));

                Assert.Null(thrown);
                Assert.StartsWith("start:warning:1x1:gap=0:", Assert.Single(h.Events));
            }
        }
        finally
        {
            TreeArranger.Gap = originalGap;
        }
    }

    /// <summary>
    /// T7 (alert-tile-mosaic, 2026-09-26): <c>UpdateAlertOverlay</c> must read <c>treeManager.
    /// Primary</c>'s work area AT SHOW TIME, not once at wire time -- the taskbar can move/auto-hide
    /// between wiring and an alert firing (feature doc). The composition's own fake display starts
    /// with no taskbar (work area == bounds, 1920x1080); this test narrows the work area (taskbar
    /// docked on TOP, 40px -- deliberately asymmetric: a Left/Top or Bounds/WorkArea argument swap in
    /// the wiring would produce (0,0,1920,1040) here instead of the correct (0,40,1920,1040), since a
    /// right-docked/origin-aligned work area used in an earlier version of this test could not tell
    /// the two apart) AFTER wiring but BEFORE sending the command, then proves the threaded
    /// <see cref="AlertShowRequest"/> carries the NEW, narrower rect, expressed relative to the
    /// display's own bounds (0,0 origin here since the fake display already starts at the desktop
    /// origin -- <see cref="AlertLayerWorkAreaTests"/> covers a non-origin monitor directly).
    /// </summary>
    [Fact]
    public void ReadsTheWorkAreaAtShowTimeAndThreadsItRelativeToTheSurface()
    {
        var h = Create();
        using (h.Composition)
        {
            h.Display.WorkArea = Rectangle.FromSize(0, 40, 1920, 1040);

            Assert.Equal(AlertPipeProtocol.OkReply, h.Server.Send("failed:1 warning:1 duration:1"));

            Assert.Equal("shake:120", h.Events[0]);
            Assert.Contains(":work=0,40,1920x1040:", h.Events[1]);
        }
    }

    /// <summary>
    /// T7: a work area that cannot be resolved (here, a degenerate/zero-size fake work area) must
    /// never fail the alert -- it falls back to <see cref="AlertLayerWorkArea.Unavailable"/> (all
    /// zero), which the page already treats as "lay out on the whole canvas", exactly pre-T7 behavior.
    /// </summary>
    [Fact]
    public void UnresolvableWorkAreaFallsBackToUnavailableRatherThanFailingTheAlert()
    {
        var h = Create();
        using (h.Composition)
        {
            h.Display.WorkArea = Rectangle.FromSize(5000, 5000, 100, 100); // Nowhere near the surface.

            var thrown = Record.Exception(() => h.Server.Send("warning:1 duration:1"));

            Assert.Null(thrown);
            Assert.Contains(":work=0,0,0x0:", Assert.Single(h.Events));
        }
    }

    /// <summary>
    /// T10 (alert-tile-mosaic, review follow-up R3-workarea-catch-path-unexercised): the previous
    /// fact proves the FALLBACK (a work area that resolves to nothing); this one proves the actual
    /// CATCH -- a display whose <c>WorkArea</c> getter throws must still degrade to <see
    /// cref="AlertLayerWorkArea.Unavailable"/>, trace <c>alert-layer-workarea-failed</c> with the real
    /// exception's type and message, and let the alert start anyway. Before this fact, the try/catch
    /// around <c>AlertLayerWorkArea.Resolve</c> in <c>AppComposition.UpdateAlertOverlay</c> had never
    /// actually been exercised by anything throwing.
    /// </summary>
    [Fact]
    public void AWorkAreaReadThatThrows_DegradesToUnavailableTracesAndStillShowsTheAlert()
    {
        var h = Create(primaryDisplay: new ThrowingWorkAreaDisplay());
        using (h.Composition)
        {
            var thrown = Record.Exception(() => h.Server.Send("warning:1 duration:1"));

            Assert.Null(thrown);
            Assert.Contains(
                h.Trace.Lines,
                line => line.Contains("alert-layer-workarea-failed", StringComparison.Ordinal)
                    && line.Contains("InvalidOperationException", StringComparison.Ordinal));
            Assert.Contains(":work=0,0,0x0:", Assert.Single(h.Events));
        }
    }

    /// <summary>Decision 3 (feature doc): failed tiles always win past the 8-tile cap, even when the command wrote warning first.</summary>
    [Fact]
    public void CommandPastTheEightTileCap_DropsWarningAndUsesTheEightPlusGrid()
    {
        var h = Create();
        using (h.Composition)
        {
            Assert.Equal(AlertPipeProtocol.OkReply, h.Server.Send("warning:3 failed:8 duration:1"));
            Assert.Equal("shake:120", h.Events[0]);
            Assert.StartsWith("start:failed,failed,failed,failed,failed,failed,failed,failed:4x2:gap=", h.Events[1]);
        }
    }

    [Fact]
    public void DisabledSettingNeverStartsLayer()
    {
        var h = Create(enabled: false);
        using (h.Composition) h.Timer.Tick();
        Assert.Empty(h.Events);
    }

    /// <summary>
    /// D3 (html-wallpaper-demo, demo/html-wallpaper-d3-switch): in html mode no player is ever started
    /// (D1/D3 wiring: TryAttach only, never TryPlay), so nothing ever sets the ordinary
    /// videoWallpaperActive flag <c>UpdateAlertOverlay</c>'s desktopVisible predicate reads. Without
    /// generalizing that predicate to also read the html-attached flag, a queued alert would sit
    /// pending forever -- this proves it actually shows once the host is attached, with no
    /// alertDesktopVisible override supplied (host is non-null here, so Create's own default leaves
    /// that override unset, exactly as WireProduction does).
    /// </summary>
    [Fact]
    public void InHtmlWallpaperMode_AnAlertShowsEvenThoughNoVideoEverPlays()
    {
        var host = new Host();
        var h = Create(host: host, wallpaperMode: WallpaperMode.Html);
        using (h.Composition)
        {
            Assert.Equal(AlertPipeProtocol.OkReply, h.Server.Send("warning:1 duration:1"));
            Assert.StartsWith("start:warning:", Assert.Single(h.Events));
        }
    }

    /// <summary>The other half of the fact above: html mode alone is not enough -- the host must have actually attached, or the alert stays held exactly as it would with no desktop visible at all.</summary>
    [Fact]
    public void InHtmlWallpaperMode_WhenTheHostNeverAttaches_AlertsStayHeld()
    {
        var host = new Host { FailAttach = true };
        var h = Create(host: host, wallpaperMode: WallpaperMode.Html);
        using (h.Composition)
        {
            Assert.Equal(AlertPipeProtocol.OkReply, h.Server.Send("warning:1 duration:1"));
            h.Timer.Tick();
            Assert.Empty(h.Events);
        }
    }

    /// <summary>
    /// remove-direct2d-alert-overlay (T1b): the only composition-level proof that a malformed pipe
    /// command is rejected without ever reaching the renderer -- ported from the deleted Direct2D
    /// <c>AlertWallpaperWiringTests.MalformedAlertCommand_ReturnsErrorAndDoesNotUpdateOverlay</c>.
    /// <c>AlertCommandParserTests</c> covers the parser alone; nothing else proves the composition
    /// wiring drops a malformed command before <c>startAlertLayer</c>.
    /// </summary>
    [Fact]
    public void MalformedAlertCommand_ReturnsErrorAndDoesNotStartTheLayer()
    {
        var h = Create();
        using (h.Composition)
        {
            var reply = h.Server.Send("nonsense");

            Assert.StartsWith("error: ", reply, StringComparison.Ordinal);

            h.Timer.Tick();

            Assert.Empty(h.Events);
            Assert.Contains(h.Trace.Lines, line => line.Contains("alert rejected", StringComparison.Ordinal));
        }
    }
}
