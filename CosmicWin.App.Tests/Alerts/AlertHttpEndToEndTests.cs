using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using CosmicWin.App.Diagnostics;
using CosmicWin.App.Input;
using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App.Tests.Alerts;

/// <summary>
/// noop-followups, F2 (review finding R3-http-202-claim-unproved on alert-busy-ignore): the ignored
/// alert's 202 is otherwise proven only through the named pipe's <c>Server.Send</c> (see
/// <c>WebViewAlertCompositionWiringTests.ASecondCommandWhileShowing_StillRepliesOk_ButIsIgnored</c>'s
/// own remark on why no separate HTTP-level test existed yet). This file drives the SAME
/// composition through the REAL <see cref="LocalHttpCommandServer"/> -- a real loopback listener, a
/// real <see cref="HttpClient"/>, no fake standing in for the HTTP transport -- the same
/// "integration: real transport, in-process server" idiom
/// <c>CosmicWin.Interop.Tests.LocalHttpCommandServerTests</c> uses, except here the server is the
/// one <c>AppComposition.Wire</c> itself constructs (<c>createLocalHttpCommandServer</c> is left at
/// its default, so the production factory builds a real <see cref="LocalHttpCommandServer"/>), so
/// the ALERT QUEUE behind it is the real, non-injectable <c>AlertQueue</c> too -- not a fake
/// standing in for the busy-ignore rule. Only the named pipe (irrelevant to this file) is stubbed
/// out, the same way <c>HttpAlertCompositionWiringTests</c> already does, so no real pipe is opened.
/// </summary>
public sealed class AlertHttpEndToEndTests
{
    private const string Token = "test-token-http-e2e";

    /// <summary>
    /// Frozen, never advanced -- this file only needs "still within the alert's own duration when
    /// the second request arrives", which holds trivially at a fixed instant regardless of how long
    /// the real HTTP round trips actually take. Mirrors
    /// <c>WebViewAlertCompositionWiringTests.FakeTimeProvider</c>, declared locally here for the
    /// same reason that file's own remarks give for its local fakes: no shared test-double project
    /// reaches both.
    /// </summary>
    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

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

    /// <summary>Stands in for the named pipe alone -- never constructed as the real
    /// <see cref="NamedPipeAlertCommandServer"/>, so this file never opens a real pipe.</summary>
    private sealed class FakePipeServer : IAlertCommandServer
    {
        public void Start()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingDesktopTrace : IDesktopTrace
    {
        public List<string> Lines { get; } = [];

        public void Record(string line) => Lines.Add(line);
    }

    private static AppComposition Wire(int port, List<string> events, RecordingDesktopTrace trace)
    {
        var primary = new FakeDisplay(
            new IntPtr(1), Rectangle.FromSize(0, 0, 1920, 1080), Rectangle.FromSize(0, 0, 1920, 1080), 1.0, true);
        var registry = new WindowRegistry();
        var treeManager = new TreeManager([primary], primary, registry);

        return AppComposition.Wire(
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
            alertsEnabled: true,
            // Only the pipe is faked -- the HTTP route below uses the real factory.
            createAlertCommandServer: (_, _, _) => new FakePipeServer(),
            alertDesktopVisible: () => true,
            startAlertLayer: (kind, duration) => events.Add($"start:{kind}:{duration}"),
            endAlertLayer: () => events.Add("end"),
            alertHttpEnabled: true,
            alertHttpPort: port,
            loadAlertHttpToken: () => Token,
            // createLocalHttpCommandServer left at its default (null): AppComposition.Wire's own
            // production fallback constructs a REAL LocalHttpCommandServer here, bound to a real
            // loopback port -- the whole point of this file over HttpAlertCompositionWiringTests.
            timeProvider: new FakeTimeProvider(DateTimeOffset.UnixEpoch));
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.Server.LocalEndPoint!).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static HttpClient NewClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return client;
    }

    private static async Task<(int Status, string Body)> PostAlertAsync(int port, string jsonBody)
    {
        using var client = NewClient();
        var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync($"http://127.0.0.1:{port}{AlertHttpProtocol.AlertsPath}", content);
        var text = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, text);
    }

    /// <summary>
    /// A1/A2 (alert-busy-ignore) decision 3: the ignored request answers EXACTLY like an accepted
    /// one -- 202 <c>ok</c> for both, over the real HTTP route this time -- while decision 1 (ANY
    /// alert is ignored, not only an identical one) and decision 4 (at most one alert exists,
    /// showing or waiting) mean the SECOND alert here (a different kind, "failed" vs "warning")
    /// never shows: only one <c>startAlertLayer</c> call is ever recorded, and the trace records the
    /// ignore.
    /// </summary>
    [Fact]
    public async Task SecondAlertWhileFirstIsShowing_BothAnswer202Ok_ButOnlyTheFirstEverShows()
    {
        var port = GetFreePort();
        var events = new List<string>();
        var trace = new RecordingDesktopTrace();

        using var composition = Wire(port, events, trace);

        var (firstStatus, firstBody) = await PostAlertAsync(port, "{\"warning\":1,\"duration\":5}");
        var (secondStatus, secondBody) = await PostAlertAsync(port, "{\"failed\":1,\"duration\":5}");

        Assert.Equal(202, firstStatus);
        Assert.Equal("ok", firstBody);
        Assert.Equal(202, secondStatus);
        Assert.Equal("ok", secondBody);

        Assert.Contains(trace.Lines, line => line.Contains("alert ignored", StringComparison.Ordinal));
        Assert.StartsWith("start:warning:", Assert.Single(events));
        Assert.DoesNotContain(events, e => e.StartsWith("start:failed:", StringComparison.Ordinal));
    }

    /// <summary>
    /// The negative control for the test above: a well-formed request that never collides with a
    /// showing/waiting alert reaches the real queue, the real overlay driver, and shows normally --
    /// proof this file's own composition (real HTTP server, real queue) works at all before trusting
    /// its ignore assertion above.
    /// </summary>
    [Fact]
    public async Task SingleAlert_Answers202OkAndShows()
    {
        var port = GetFreePort();
        var events = new List<string>();
        var trace = new RecordingDesktopTrace();

        using var composition = Wire(port, events, trace);

        var (status, body) = await PostAlertAsync(port, "{\"warning\":2,\"duration\":5}");

        Assert.Equal(202, status);
        Assert.Equal("ok", body);
        Assert.StartsWith("start:warning:", Assert.Single(events));
        Assert.DoesNotContain(trace.Lines, line => line.Contains("alert ignored", StringComparison.Ordinal));
    }
}
