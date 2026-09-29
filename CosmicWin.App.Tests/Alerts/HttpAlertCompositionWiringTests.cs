using CosmicWin.App.Diagnostics;
using CosmicWin.App.Input;
using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.App.Tray;
using CosmicWin.Interop;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App.Tests.Alerts;

/// <summary>
/// H4 (http-alert-endpoint): proves <c>AppComposition.Wire</c> starts <see
/// cref="LocalHttpCommandServer"/> alongside the named pipe -- gated on BOTH <c>alertsEnabled</c> and
/// the new <c>alertHttpEnabled</c> flag, sharing the exact same <c>HandleAlertCommand</c> closure and
/// <c>desktopTrace</c> sink the pipe uses, tolerant of a missing token or a failing HTTP server
/// (which must never take the pipe down with it), and disposed together with the pipe. The real
/// <see cref="LocalHttpCommandServer"/>'s own request-gate/protocol behaviour is covered by
/// <c>CosmicWin.Interop.Tests</c>; this file only proves the composition wiring around the
/// <c>createLocalHttpCommandServer</c>/<c>loadAlertHttpToken</c> seams.
/// </summary>
public sealed class HttpAlertCompositionWiringTests
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
        public IDisposable Schedule(TimeSpan interval, Action callback) => new NullDisposable();
    }

    /// <summary>Fake for BOTH the pipe and the HTTP server -- <see cref="IAlertCommandServer"/> is
    /// the same seam either transport is handed through.</summary>
    private sealed class FakeServer(Func<string, string> handle) : IAlertCommandServer
    {
        public bool Started { get; private set; }
        public bool Disposed { get; private set; }
        public void Start() => Started = true;
        public string Send(string command) => handle(command);
        public void Dispose() => Disposed = true;
    }

    private sealed class ThrowingStartServer : IAlertCommandServer
    {
        public void Start() => throw new InvalidOperationException("start failed");
        public void Dispose()
        {
        }
    }

    private sealed class RecordingDesktopTrace : IDesktopTrace
    {
        public List<string> Lines { get; } = [];
        public void Record(string line) => Lines.Add(line);
    }

    private sealed record Harness(
        AppComposition Composition, FakeServer? Pipe, RecordingDesktopTrace Trace,
        List<string> HttpFactoryCalls, int TokenLoadCalls, Func<string, string>? HttpHandler,
        Func<string, bool>? HttpVideoSwitchHandler, Func<string, bool>? HttpSceneSwitchHandler,
        IAlertCommandServer? HttpServer, List<string> Events);

    private static Harness Wire(
        bool alertsEnabled = true, bool httpEnabled = false, int httpPort = 47811,
        string? token = "test-token",
        Func<int, string, Func<string, string>?, Action<string>?, Func<string, bool>?, Func<string, bool>?, IAlertCommandServer>? httpFactory = null)
    {
        var primary = new FakeDisplay(
            new IntPtr(1), Rectangle.FromSize(0, 0, 1920, 1080), Rectangle.FromSize(0, 0, 1920, 1080), 1.0, true);
        var registry = new WindowRegistry();
        var treeManager = new TreeManager([primary], primary, registry);
        var trace = new RecordingDesktopTrace();
        FakeServer? pipe = null;
        var httpFactoryCalls = new List<string>();
        var tokenLoadCalls = 0;
        Func<string, string>? httpHandler = null;
        Func<string, bool>? httpVideoSwitchHandler = null;
        Func<string, bool>? httpSceneSwitchHandler = null;
        IAlertCommandServer? httpServer = null;
        var events = new List<string>();

        var resolvedHttpFactory = httpFactory ?? ((port, tok, handler, diagnostic, videoSwitch, sceneSwitch) =>
        {
            httpFactoryCalls.Add($"port={port} token={tok}");
            httpHandler = handler;
            httpVideoSwitchHandler = videoSwitch;
            httpSceneSwitchHandler = sceneSwitch;
            var server = new FakeServer(handler ?? (_ => throw new InvalidOperationException(
                "the alerts route is off -- this fake never routes to it")));
            httpServer = server;
            return server;
        });

        var composition = AppComposition.Wire(
            new FakeWorkspace(), treeManager, registry, new NoForeground(),
            new ExceptionListStore(ExceptionList.Empty),
            focusTrace: new RecordingFocusTrace(),
            disableTaskTrigger: () => { },
            scheduleReconcile: new Scheduler().Schedule,
            hookFactory: writer => new LowLevelKeyboardHook(
                writer, new FakeKeyboardHookPlatform(), TimeSpan.FromSeconds(5), () => 0),
            loadExceptions: () => ExceptionList.Empty,
            shutdown: () => { },
            buildTray: _ => new NullDisposable(),
            importVideoWallpaper: p => p,
            desktopTrace: trace,
            alertsEnabled: alertsEnabled,
            createAlertCommandServer: (_, handle, _) => pipe = new FakeServer(handle),
            alertDesktopVisible: () => true,
            // alert-tile-mosaic (2026-09-26): the single-kind "start:{kind}:{duration}" contract was
            // rewritten, not silently dropped, to AlertShowRequest -- this file only greps for the
            // "start:{kind}:" prefix its assertions already use, so keeping that exact prefix (with
            // the grid/gap/duration appended) needs no assertion changes here.
            startAlertLayer: request => events.Add(
                $"start:{string.Join(",", request.Tiles)}:{request.Columns}x{request.Rows}:gap={request.Gap}:{request.DurationMilliseconds}"),
            endAlertLayer: () => events.Add("end"),
            httpServerEnabled: httpEnabled,
            httpServerPort: httpPort,
            createLocalHttpCommandServer: resolvedHttpFactory,
            loadAlertHttpToken: () =>
            {
                tokenLoadCalls++;
                return token;
            });

        return new Harness(
            composition, pipe, trace, httpFactoryCalls, tokenLoadCalls, httpHandler,
            httpVideoSwitchHandler, httpSceneSwitchHandler, httpServer, events);
    }

    [Fact]
    public void HttpDisabled_FactoryNeverCalledAndTokenNeverLoaded()
    {
        var h = Wire(alertsEnabled: true, httpEnabled: false);
        using (h.Composition)
        {
            Assert.Empty(h.HttpFactoryCalls);
            Assert.Equal(0, h.TokenLoadCalls);
            Assert.True(h.Pipe!.Started);
        }
    }

    /// <summary>
    /// The single http-server switch serves every route: with alerts fully disabled the shared
    /// server still starts, carrying no alert handler but the video and scene handlers.
    /// </summary>
    [Fact]
    public void ServerOn_AlertsDisabled_StartsHttpServerWithVideoAndSceneButNoAlertHandler()
    {
        var h = Wire(alertsEnabled: false, httpEnabled: true, httpPort: 6001);
        using (h.Composition)
        {
            Assert.Equal(["port=6001 token=test-token"], h.HttpFactoryCalls);
            Assert.Null(h.HttpHandler);
            Assert.NotNull(h.HttpVideoSwitchHandler);
            Assert.NotNull(h.HttpSceneSwitchHandler);
            Assert.True(((FakeServer)h.HttpServer!).Started);
            Assert.DoesNotContain(
                h.Trace.Lines, l => l.StartsWith("alert-http start requested", StringComparison.Ordinal));
            Assert.Contains(
                h.Trace.Lines,
                l => l == "http-server start requested port=6001 alerts-route=False video-route=True "
                    + "scene-route=True");
        }
    }

    /// <summary>Server on and alerts on: all three routes are in the table, with no per-route toggles.</summary>
    [Fact]
    public void ServerOn_AlertsEnabled_ServesEveryRoute()
    {
        var h = Wire(alertsEnabled: true, httpEnabled: true, httpPort: 6003);
        using (h.Composition)
        {
            Assert.NotNull(h.HttpHandler);
            Assert.NotNull(h.HttpVideoSwitchHandler);
            Assert.NotNull(h.HttpSceneSwitchHandler);
            Assert.Contains(h.Trace.Lines, l => l == "alert-http start requested port=6003");
            Assert.Contains(
                h.Trace.Lines,
                l => l == "http-server start requested port=6003 alerts-route=True video-route=True "
                    + "scene-route=True");
        }
    }

    /// <summary>Server off, alerts enabled: the shared server never starts -- the pipe alone is not
    /// enough to bring it up.</summary>
    [Fact]
    public void ServerOff_HttpServerNeverStartedButPipeDoes()
    {
        var h = Wire(alertsEnabled: true, httpEnabled: false);
        using (h.Composition)
        {
            Assert.Empty(h.HttpFactoryCalls);
            Assert.Equal(0, h.TokenLoadCalls);
            Assert.True(h.Pipe!.Started);
            Assert.DoesNotContain(
                h.Trace.Lines, l => l.StartsWith("http-server start requested", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void BothEnabled_StartsHttpServerWithSettingsPortAndLoadedTokenAndSharesTheQueue()
    {
        var h = Wire(alertsEnabled: true, httpEnabled: true, httpPort: 5001, token: "abc123");
        using (h.Composition)
        {
            Assert.Equal(["port=5001 token=abc123"], h.HttpFactoryCalls);
            Assert.Equal(1, h.TokenLoadCalls);
            Assert.True(((FakeServer)h.HttpServer!).Started);

            // Same handler as the pipe -- the reply and the layer start prove it routes through the
            // SAME alert queue, not a second one.
            var reply = h.HttpHandler!("warning:1");
            Assert.Equal(AlertPipeProtocol.OkReply, reply);
            Assert.StartsWith("start:warning:", Assert.Single(h.Events));

            Assert.Contains(h.Trace.Lines, l => l == "alert-http start requested port=5001");
            Assert.DoesNotContain(h.Trace.Lines, l => l.Contains("abc123", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void NullToken_DoesNotStartHttpServerAndTracesWithoutTheTokenButPipeStillStarts()
    {
        var h = Wire(alertsEnabled: true, httpEnabled: true, token: null);
        using (h.Composition)
        {
            Assert.Empty(h.HttpFactoryCalls);
            Assert.True(h.Pipe!.Started);
            Assert.Contains(h.Trace.Lines, l => l.Contains("alert-http", StringComparison.Ordinal)
                && l.Contains("token", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void HttpFactoryThrows_PipeStillStartedAndComposingContinues()
    {
        var h = Wire(alertsEnabled: true, httpEnabled: true,
            httpFactory: (_, _, _, _, _, _) => throw new InvalidOperationException("factory boom"));
        using (h.Composition)
        {
            Assert.True(h.Pipe!.Started);
            // NOT Assert.Null(h.HttpServer) here: a CUSTOM httpFactory (this one) bypasses the
            // harness's own capturing closure entirely -- h.HttpServer is only ever assigned inside
            // Wire's DEFAULT factory, so it would read null regardless of whether the HTTP server
            // actually started. The trace lines below are what actually prove the failure path ran.
            Assert.Contains(h.Trace.Lines, l => l.StartsWith("alert-http-start-failed", StringComparison.Ordinal));
            Assert.DoesNotContain(h.Trace.Lines, l => l.StartsWith("alert-http start requested", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void HttpServerStartThrows_PipeStillStartedAndComposingContinues()
    {
        var h = Wire(alertsEnabled: true, httpEnabled: true,
            httpFactory: (_, _, _, _, _, _) => new ThrowingStartServer());
        using (h.Composition)
        {
            Assert.True(h.Pipe!.Started);
            Assert.Contains(h.Trace.Lines, l => l.StartsWith("alert-http-start-failed", StringComparison.Ordinal));
            Assert.DoesNotContain(h.Trace.Lines, l => l.StartsWith("alert-http start requested", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void BothServersAreDisposedOnShutdown()
    {
        var h = Wire(alertsEnabled: true, httpEnabled: true);
        h.Composition.Dispose();

        Assert.True(h.Pipe!.Disposed);
        Assert.True(((FakeServer)h.HttpServer!).Disposed);
    }
}
