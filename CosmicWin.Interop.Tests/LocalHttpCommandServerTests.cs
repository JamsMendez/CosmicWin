using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using CosmicWin.Interop;
using Xunit.Abstractions;

namespace CosmicWin.Interop.Tests;

/// <summary>
/// Integration coverage for <see cref="LocalHttpCommandServer"/>: a real loopback
/// <see cref="System.Net.HttpListener"/>, a real <see cref="HttpClient"/>, no fakes -- the same
/// "integration: real transport, in-process server" idiom
/// <c>NamedPipeAlertCommandServerTests</c> uses for the pipe half of this feature. Every fact gets
/// its own free loopback port (<see cref="GetFreePort"/>) even though <c>TestParallelism.cs</c>
/// already serialises this assembly, for the same belt-and-braces reason the pipe tests give every
/// fact its own pipe name: a leftover listener from a previous run must never collide with this one.
/// </summary>
public sealed class LocalHttpCommandServerTests(ITestOutputHelper output)
{
    private const string Token = "test-token-abc123";
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);

    // ---- constructor validation ----

    [Fact]
    public void Constructor_PortZero_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new LocalHttpCommandServer(0, Token, _ => "ok"));

    [Fact]
    public void Constructor_PortTooLarge_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new LocalHttpCommandServer(65536, Token, _ => "ok"));

    [Fact]
    public void Constructor_NullToken_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new LocalHttpCommandServer(GetFreePort(), null!, _ => "ok"));

    [Fact]
    public void Constructor_BlankToken_Throws() =>
        Assert.Throws<ArgumentException>(() => new LocalHttpCommandServer(GetFreePort(), "   ", _ => "ok"));

    // V3: a null handleCommand no longer throws -- it means the alerts route is off, modelled the
    // same way the video route's enablement is (see the "per-route enablement" region below). The
    // old assertion (throws ArgumentNullException) described a stricter contract this class no
    // longer has, now that decision 2 requires each route to be independently switchable.
    [Fact]
    public void Constructor_NullHandlerAndNullVideoSwitch_DoesNotThrow()
    {
        var exception = Record.Exception(() => new LocalHttpCommandServer(GetFreePort(), Token, null));
        Assert.Null(exception);
    }

    // R3-001 (review-3 follow-up): the constructor check above only proves the object comes into
    // existence -- it never starts a server or shows both routes are actually off. This proves the
    // contract: with BOTH delegates null, a well-formed, authorized POST to EACH route answers
    // exactly what a genuinely unknown path answers (same status, same body), not just "some 4xx".
    [Fact]
    public async Task BothRoutesDisabled_AlertsRouteAnswersExactlyLikeAnUnknownPath()
    {
        var port = GetFreePort();
        using var server = new LocalHttpCommandServer(port, Token, handleCommand: null, msg => output.WriteLine(msg));
        server.Start();

        using var client = NewClient();
        var (unknownStatus, unknownBody) = await GetAgainstUnknownPath(client, port);
        using var alertResponse = await client.PostAsync(
            $"http://127.0.0.1:{port}{AlertHttpProtocol.AlertsPath}", JsonContent("{\"warning\":1}"));

        Assert.Equal((HttpStatusCode)unknownStatus, alertResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, alertResponse.StatusCode);
        Assert.Equal(unknownBody, await alertResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task BothRoutesDisabled_VideoRouteAnswersExactlyLikeAnUnknownPath()
    {
        var port = GetFreePort();
        using var server = new LocalHttpCommandServer(port, Token, handleCommand: null, msg => output.WriteLine(msg));
        server.Start();

        using var client = NewClient();
        var (unknownStatus, unknownBody) = await GetAgainstUnknownPath(client, port);
        using var videoResponse = await client.PostAsync(
            $"http://127.0.0.1:{port}{VideoWallpaperHttpProtocol.VideoPath}", VideoJsonContent(VideoBody(ExistingVideo)));

        Assert.Equal((HttpStatusCode)unknownStatus, videoResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, videoResponse.StatusCode);
        Assert.Equal(unknownBody, await videoResponse.Content.ReadAsStringAsync());
    }

    // ---- lifecycle ----

    [Fact]
    public async Task Start_Idempotent_SecondCallStillServesNormally()
    {
        var port = GetFreePort();
        using var server = new LocalHttpCommandServer(port, Token, _ => "ok");
        server.Start();
        server.Start(); // must be a no-op, not a re-bind attempt

        var (status, body) = await PostAsync(port, "{\"warning\":1}");

        Assert.Equal(202, status);
        Assert.Equal("ok", body);
    }

    [Fact]
    public void Start_PortAlreadyInUse_ReportsDiagnosticAndDoesNotThrow()
    {
        var port = GetFreePort();
        using var occupant = new LocalHttpCommandServer(port, Token, _ => "ok");
        occupant.Start();

        var diagnostics = new List<string>();
        using var server = new LocalHttpCommandServer(port, Token, _ => "ok", diagnostics.Add);

        var exception = Record.Exception(server.Start);

        Assert.Null(exception);
        Assert.NotEmpty(diagnostics);
    }

    [Fact]
    public void Dispose_BeforeStart_IsSafe()
    {
        var server = new LocalHttpCommandServer(GetFreePort(), Token, _ => "ok");
        var exception = Record.Exception(server.Dispose);
        Assert.Null(exception);
    }

    [Fact]
    public void Dispose_Idempotent()
    {
        var server = new LocalHttpCommandServer(GetFreePort(), Token, _ => "ok");
        server.Start();
        server.Dispose();
        var exception = Record.Exception(server.Dispose);
        Assert.Null(exception);
    }

    [Fact]
    public async Task Dispose_StopsAccepting()
    {
        var port = GetFreePort();
        var server = new LocalHttpCommandServer(port, Token, _ => "ok");
        server.Start();

        // Prove it really was accepting first.
        var (status, _) = await PostAsync(port, "{\"warning\":1}");
        Assert.Equal(202, status);

        server.Dispose();

        using var client = NewClient();
        await Assert.ThrowsAnyAsync<Exception>(() => client.PostAsync(
            $"http://127.0.0.1:{port}{AlertHttpProtocol.AlertsPath}", JsonContent("{\"warning\":1}")));
    }

    // ---- request gate ----

    [Fact]
    public async Task OriginHeaderPresent_Returns403()
    {
        var port = GetFreePort();
        using var server = Start(port, _ => "ok");

        using var request = NewRequest(port, HttpMethod.Post, "{\"warning\":1}");
        request.Headers.Add("Origin", "http://evil.example");

        using var client = NewClient();
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.StartsWith("error: ", await response.Content.ReadAsStringAsync());
    }

    // ---- R3-001/002: raw-socket Host-header measurement ----
    //
    // A review of H2 assumed http.sys would answer `Host: localhost:{port}` and
    // `Host: evil.example:1234` itself with its own 400 "Invalid Hostname", before HandleRequest ever
    // ran, reasoning from a single literal (non-wildcard) prefix. MEASURED 2026-09-25 with a raw
    // TcpClient against a listener bound only to `http://127.0.0.1:{port}/` (no client library
    // involved, so nothing could rewrite the Host header):
    //
    //   Host: localhost:{port}        -> reached HandleRequest (UserHostName == "localhost:{port}"),
    //                                     replied 202 "ok" -- OUR code accepted it (IsAllowedHost).
    //   Host: 127.0.0.1:{port}        -> reached HandleRequest, replied 202 "ok".
    //   Host: evil.example:1234       -> reached HandleRequest (UserHostName == "evil.example:1234",
    //                                     Request.Url.Host == "evil.example"), replied 403
    //                                     "error: unexpected host header" -- OUR IsAllowedHost check
    //                                     rejected it. http.sys never answered on its own.
    //
    // Conclusion: with a single literal prefix registered, http.sys performs NO Host-header
    // filtering at all -- it forwards every request on that port to this listener regardless of what
    // Host says, because there is no ambiguity between competing prefixes for it to resolve. The
    // application-level Host check (gate step 3) is therefore the ENTIRE defense against DNS
    // rebinding here, not defense in depth layered on top of an http.sys-level filter. The three
    // facts below prove exactly that: OUR layer answers every one of these, never http.sys.
    //
    // Separately: a REAL caller typing the literal URL `http://localhost:{port}/...` does a DNS
    // resolution of "localhost" first, and on this machine that resolves to the IPv6 loopback
    // address (verified: HttpListener bound additionally to the `localhost` prefix reported
    // Request.LocalEndPoint == [::1]:{port}) -- a request that a listener bound ONLY to the IPv4
    // literal `127.0.0.1` prefix never even receives at the socket level (measured: it timed out,
    // never reaching HandleRequest at all). That is a real, separate gap from the Host-HEADER
    // question above, and it is why Start() below registers the `localhost` prefix too.

    [Fact]
    public async Task RawHost_ArbitraryHostname_IsRejectedByOurOwnCheckWith403()
    {
        var port = GetFreePort();
        using var server = Start(port, _ => "ok");

        var (status, body) = await SendRawAsync(port, BuildRawRequest(AlertHttpProtocol.AlertsPath, "evil.example:1234", "{\"warning\":1}"));

        Assert.Equal(403, status);
        Assert.Equal(AlertPipeProtocol.FormatError("unexpected host header"), body);
    }

    [Fact]
    public async Task RawHost_ExactLoopbackIp_ReachesOurHandlerAndReturns202()
    {
        var port = GetFreePort();
        using var server = Start(port, _ => "ok");

        var (status, body) = await SendRawAsync(port, BuildRawRequest(AlertHttpProtocol.AlertsPath, $"127.0.0.1:{port}", "{\"warning\":1}"));

        Assert.Equal(202, status);
        Assert.Equal("ok", body);
    }

    [Fact]
    public async Task RawHost_LocalhostAlias_ReachesOurHandlerAndReturns202()
    {
        var port = GetFreePort();
        using var server = Start(port, _ => "ok");

        var (status, body) = await SendRawAsync(port, BuildRawRequest(AlertHttpProtocol.AlertsPath, $"localhost:{port}", "{\"warning\":1}"));

        Assert.Equal(202, status);
        Assert.Equal("ok", body);
    }

    /// <summary>
    /// The gap the raw Host-HEADER facts above cannot show: a caller that types the literal URL
    /// <c>http://localhost:{port}/...</c> resolves "localhost" through DNS first, and never sends a
    /// raw request to 127.0.0.1 at all if that resolves elsewhere. MEASURED 2026-09-25: on this
    /// machine "localhost" resolves to the IPv6 loopback address, so this real end-to-end path needs
    /// the `localhost` prefix actually registered, not just accepted at the Host-header gate.
    /// </summary>
    [Fact]
    public async Task LiteralLocalhostUrl_ViaRealDnsResolution_ReachesTheServer()
    {
        var port = GetFreePort();
        using var server = Start(port, _ => "ok");

        using var client = NewClient();
        client.Timeout = TimeSpan.FromSeconds(5);
        using var response = await client.PostAsync(
            $"http://localhost:{port}{AlertHttpProtocol.AlertsPath}", JsonContent("{\"warning\":1}"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task WrongPath_Returns404()
    {
        var port = GetFreePort();
        using var server = Start(port, _ => "ok");

        using var client = NewClient();
        using var response = await client.PostAsync($"http://127.0.0.1:{port}/v1/other", JsonContent("{\"warning\":1}"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task WrongMethod_Returns405WithAllowHeader()
    {
        var port = GetFreePort();
        using var server = Start(port, _ => "ok");

        using var request = NewRequest(port, HttpMethod.Get, body: null);
        using var client = NewClient();
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Contains("POST", response.Content.Headers.Allow);
    }

    /// <summary>
    /// A bare OPTIONS request (no <c>Origin</c>) exercises the method check (gate step 5) on its own:
    /// it is rejected exactly like any other non-POST method, 405 with <c>Allow: POST</c>, and never
    /// answered with an <c>Access-Control-*</c> header. A REAL browser CORS preflight always also
    /// carries an <c>Origin</c> header, which the gate's earlier step 2 already rejects with 403 (see
    /// <see cref="OriginHeaderPresent_Returns403"/>) -- reached before the method is even looked at.
    /// Either way, no code path in this class ever writes an <c>Access-Control-*</c> header, so a
    /// preflight is never answered with one.
    /// </summary>
    [Fact]
    public async Task OptionsWithNoOrigin_Returns405AndNeverAnswersWithCorsHeaders()
    {
        var port = GetFreePort();
        using var server = Start(port, _ => "ok");

        using var request = NewRequest(port, HttpMethod.Options, body: null);

        using var client = NewClient();
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Contains("POST", response.Content.Headers.Allow);
        Assert.DoesNotContain(response.Headers, h => h.Key.StartsWith("Access-Control", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MissingAuthorization_Returns401WithWwwAuthenticate()
    {
        var port = GetFreePort();
        using var server = Start(port, _ => "ok");

        using var client = NewClientWithoutAuth();
        using var response = await client.PostAsync(
            $"http://127.0.0.1:{port}{AlertHttpProtocol.AlertsPath}", JsonContent("{\"warning\":1}"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, v => v.Scheme == "Bearer");
    }

    [Fact]
    public async Task WrongToken_Returns401()
    {
        var port = GetFreePort();
        using var server = Start(port, _ => "ok");

        using var client = NewClientWithoutAuth();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-the-token");
        using var response = await client.PostAsync(
            $"http://127.0.0.1:{port}{AlertHttpProtocol.AlertsPath}", JsonContent("{\"warning\":1}"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WrongContentType_Returns415()
    {
        var port = GetFreePort();
        using var server = Start(port, _ => "ok");

        using var client = NewClient();
        var content = new StringContent("{\"warning\":1}", Encoding.UTF8, "text/plain");
        using var response = await client.PostAsync($"http://127.0.0.1:{port}{AlertHttpProtocol.AlertsPath}", content);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task DeclaredContentLengthTooLarge_Returns413()
    {
        var port = GetFreePort();
        var handlerCalls = 0;
        using var server = Start(port, _ => { Interlocked.Increment(ref handlerCalls); return "ok"; });

        var oversizedJson = "{\"warning\":" + new string('1', AlertHttpProtocol.MaxBodyBytes + 100) + "}";
        using var client = NewClient();
        using var response = await client.PostAsync(
            $"http://127.0.0.1:{port}{AlertHttpProtocol.AlertsPath}", JsonContent(oversizedJson));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, handlerCalls);
    }

    [Fact]
    public async Task ChunkedBodyTooLarge_Returns413()
    {
        var port = GetFreePort();
        using var server = Start(port, _ => "ok");

        var oversizedJson = "{\"warning\":" + new string('1', AlertHttpProtocol.MaxBodyBytes + 100) + "}";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}{AlertHttpProtocol.AlertsPath}")
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(oversizedJson))),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TransferEncodingChunked = true;

        using var client = NewClient();
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task BadUtf8Body_Returns400()
    {
        var port = GetFreePort();
        using var server = Start(port, _ => "ok");

        var invalidUtf8 = new byte[] { 0xFF, 0xFE, 0x00, 0x01 };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}{AlertHttpProtocol.AlertsPath}")
        {
            Content = new ByteArrayContent(invalidUtf8),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var client = NewClient();
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task BadJsonBody_Returns400()
    {
        var port = GetFreePort();
        using var server = Start(port, _ => "ok");

        var (status, body) = await PostAsync(port, "not-json");

        Assert.Equal(400, status);
        Assert.StartsWith("error: ", body);
    }

    [Fact]
    public async Task ValidRequest_HandlerReceivesTheTranslatedCommandText_Returns202()
    {
        var port = GetFreePort();
        string? received = null;
        using var server = Start(port, text => { received = text; return "ok"; });

        var (status, body) = await PostAsync(port, "{\"warning\":2,\"failed\":1}", contentType: "application/json; charset=utf-8");

        Assert.Equal(202, status);
        Assert.Equal("ok", body);
        Assert.Equal("warning:2 failed:1", received);
    }

    [Fact]
    public async Task HandlerReplyAlertsDisabled_PassesThroughAs503()
    {
        var port = GetFreePort();
        using var server = Start(port, _ => AlertPipeProtocol.FormatError("alerts are disabled"));

        var (status, body) = await PostAsync(port, "{\"warning\":1}");

        Assert.Equal(503, status);
        Assert.Equal(AlertPipeProtocol.FormatError("alerts are disabled"), body);
    }

    [Fact]
    public async Task HandlerThrows_Returns500AndTheLoopKeepsServing()
    {
        var port = GetFreePort();
        var diagnostics = new List<string>();
        var first = true;
        using var server = new LocalHttpCommandServer(port, Token, _ =>
        {
            if (first)
            {
                first = false;
                throw new InvalidOperationException("boom");
            }

            return "ok";
        }, diagnostics.Add);
        server.Start();

        var (firstStatus, firstBody) = await PostAsync(port, "{\"warning\":1}");
        Assert.Equal(500, firstStatus);
        Assert.Equal(AlertPipeProtocol.FormatError("internal error"), firstBody);
        Assert.NotEmpty(diagnostics);

        var (secondStatus, secondBody) = await PostAsync(port, "{\"warning\":1}");
        Assert.Equal(202, secondStatus);
        Assert.Equal("ok", secondBody);
    }

    [RequiresNonLoopbackIPv4Fact]
    public async Task ConnectingThroughANonLoopbackAddress_IsRejectedOrRefused()
    {
        var address = RequiresNonLoopbackIPv4FactAttribute.NonLoopbackIPv4!;
        var port = GetFreePort();
        using var server = Start(port, _ => "ok");

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"http://{address}:{port}{AlertHttpProtocol.AlertsPath}")
        {
            Content = JsonContent("{\"warning\":1}"),
        };
        request.Headers.Host = $"127.0.0.1:{port}";

        using var client = NewClient();
        client.Timeout = TimeSpan.FromSeconds(3);

        try
        {
            using var response = await client.SendAsync(request);
            // MEASURED 2026-09-25: with TWO literal hostname prefixes registered (127.0.0.1 and
            // localhost), http.sys itself now validates a connection that arrived on neither
            // registered local address -- a LAN NIC gets turned away with http.sys's own
            // "400 Bad Request - Invalid Hostname" page, before this class ever sees it. That is a
            // DIFFERENT outcome than the single-prefix measurement in the class remarks (where every
            // Host header reached HandleRequest unfiltered), because http.sys only needs to
            // disambiguate between competing prefixes once there is more than one. Either 403 (this
            // class's own RemoteEndPoint check, if http.sys ever did route it here) or any other 4xx
            // (http.sys's own rejection) proves the same thing: this LAN connection is refused one
            // way or the other, never accepted.
            Assert.True(
                (int)response.StatusCode is >= 400 and < 500,
                $"Expected some 4xx rejection, got {(int)response.StatusCode} {response.StatusCode}.");
            output.WriteLine($"LAN connection through {address} got {(int)response.StatusCode} {response.StatusCode}.");
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            // Also acceptable: the connection never completed at all -- refused or unreachable.
            output.WriteLine($"Connection through {address} was refused/unreachable: {error.Message}");
        }
    }

    // ---- R3-003: the pure loopback-remote check, unit-tested deterministically ----

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.5.6.7", true)]
    [InlineData("::1", true)]
    [InlineData("10.0.0.5", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("203.0.113.4", false)]
    public void IsLoopbackRemote_RecognisesLoopbackAddresses(string address, bool expected)
    {
        var endpoint = new IPEndPoint(IPAddress.Parse(address), 12345);
        Assert.Equal(expected, LocalHttpCommandServer.IsLoopbackRemote(endpoint));
    }

    [Fact]
    public void IsLoopbackRemote_Ipv4MappedToIPv6Loopback_IsLoopback()
    {
        // The shape a dual-stack socket can hand back for an IPv4 peer (::ffff:127.0.0.1), even
        // though this listener's own prefixes are IPv4/hostname literals, never "::" or "+".
        var mapped = IPAddress.Parse("127.0.0.1").MapToIPv6();
        var endpoint = new IPEndPoint(mapped, 12345);

        Assert.True(LocalHttpCommandServer.IsLoopbackRemote(endpoint));
    }

    [Fact]
    public void IsLoopbackRemote_Ipv4MappedToIPv6NonLoopback_IsNotLoopback()
    {
        var mapped = IPAddress.Parse("10.0.0.5").MapToIPv6();
        var endpoint = new IPEndPoint(mapped, 12345);

        Assert.False(LocalHttpCommandServer.IsLoopbackRemote(endpoint));
    }

    [Fact]
    public void IsLoopbackRemote_NullEndpoint_IsNotLoopback() =>
        Assert.False(LocalHttpCommandServer.IsLoopbackRemote(null));

    // ---- R3-005: bounded backoff if GetContext keeps throwing while still listening ----
    //
    // NamedPipeAlertCommandServer's RunLoop can be driven into repeated, non-terminal failures
    // deterministically: a second pipe server on the SAME pipe name collides on every single
    // CreateNamedPipe call, because that resource is exclusively named and nMaxInstances = 1. There
    // is no HTTP equivalent -- an HttpListener bound to one prefix does not "collide" with anything
    // once Start() has succeeded, and every realistic way GetContext() can fail after that (Stop(),
    // Close(), or the listener being disposed) is exactly the shutdown path already handled by the
    // `when (!listener.IsListening)` branch, not a repeating, in-place failure. Nothing short of
    // reaching into http.sys internals reproduces a genuinely repeating GetContext() failure while
    // IsListening stays true. The backoff below is still added, mirroring
    // NamedPipeAlertCommandServer's InitialRetryBackoff/MaxRetryBackoff idiom, as a defensive measure
    // against a failure mode that cannot be provoked from a black-box test -- this is a deliberate
    // gap in coverage, not an oversight.

    // ---- R3-006: the Bearer scheme is case-insensitive; the token itself is not ----

    [Fact]
    public async Task BearerSchemeLowercase_IsAccepted()
    {
        var port = GetFreePort();
        using var server = Start(port, _ => "ok");

        var (status, body) = await SendRawAsync(
            port, BuildRawRequest(AlertHttpProtocol.AlertsPath, $"127.0.0.1:{port}", "{\"warning\":1}", authorization: $"bearer {Token}"));

        Assert.Equal(202, status);
        Assert.Equal("ok", body);
    }

    // ---- R3-004: Start after Dispose must be a no-op, and the port must stay free ----

    [Fact]
    public async Task Start_AfterDispose_IsANoOpAndLeavesThePortFree()
    {
        var port = GetFreePort();
        var server = new LocalHttpCommandServer(port, Token, _ => "ok");

        // Never started, THEN disposed (finding R3-004): an instance that was started first already
        // has a thread, and Start's `_thread is not null` guard would hide a missing `_disposed` check.
        server.Dispose();

        // Must be a genuine no-op: no exception, and -- the actual proof -- the port comes back
        // free, because Start() did not quietly re-bind it.
        var exception = Record.Exception(server.Start);
        Assert.Null(exception);

        using var probe = new TcpListener(IPAddress.Loopback, port);
        var bindException = Record.Exception(probe.Start);
        probe.Stop();
        Assert.Null(bindException);

        // And a well-behaved client genuinely gets nothing back -- this instance stayed inert.
        using var client = NewClient();
        await Assert.ThrowsAnyAsync<Exception>(() => client.PostAsync(
            $"http://127.0.0.1:{port}{AlertHttpProtocol.AlertsPath}", JsonContent("{\"warning\":1}")));
    }

    // ---- R3-007: boundary tests around MaxBodyBytes ----

    [Fact]
    public async Task BodyExactlyAtTheCap_ValidJsonPaddedWithWhitespace_Returns202()
    {
        var port = GetFreePort();
        using var server = Start(port, _ => "ok");

        const string prefix = "{\"warning\":1";
        const string suffix = "}";
        var padding = new string(' ', AlertHttpProtocol.MaxBodyBytes - prefix.Length - suffix.Length);
        var body = prefix + padding + suffix;
        Assert.Equal(AlertHttpProtocol.MaxBodyBytes, Encoding.UTF8.GetByteCount(body));

        var (status, responseBody) = await PostAsync(port, body);

        Assert.Equal(202, status);
        Assert.Equal("ok", responseBody);
    }

    [Fact]
    public async Task BodyOneByteOverTheCap_Returns413()
    {
        var port = GetFreePort();
        var handlerCalls = 0;
        using var server = Start(port, _ => { Interlocked.Increment(ref handlerCalls); return "ok"; });

        const string prefix = "{\"warning\":1";
        const string suffix = "}";
        var padding = new string(' ', AlertHttpProtocol.MaxBodyBytes - prefix.Length - suffix.Length + 1);
        var body = prefix + padding + suffix;
        Assert.Equal(AlertHttpProtocol.MaxBodyBytes + 1, Encoding.UTF8.GetByteCount(body));

        var (status, _) = await PostAsync(port, body);

        Assert.Equal(413, status);
        Assert.Equal(0, handlerCalls);
    }

    [Fact]
    public async Task TrulyChunkedBody_RawSocket_OverTheCap_Returns413()
    {
        var port = GetFreePort();
        using var server = Start(port, _ => "ok");

        const string prefix = "{\"warning\":1";
        const string suffix = "}";
        var padding = new string(' ', AlertHttpProtocol.MaxBodyBytes - prefix.Length - suffix.Length + 50);
        var body = prefix + padding + suffix;

        var (status, responseBody) = await SendRawAsync(
            port, BuildRawChunkedRequest(AlertHttpProtocol.AlertsPath, $"127.0.0.1:{port}", body));

        Assert.Equal(413, status);
        Assert.StartsWith("error: ", responseBody);
    }

    // ---- R3-002 (review-3 follow-up): the per-route body cap must also govern the UNKNOWN-length
    // (chunked, no Content-Length) read loop in TryReadBody, not just the declared-Content-Length
    // check. BodyBetweenTheAlertCapAndTheVideoCap_Is413OnAlertsButNotOnVideo above already proves
    // this for a DECLARED length; these three prove it for genuinely chunked wire framing, built
    // byte-for-byte with BuildRawChunkedRequest (no Content-Length header at all -- HttpClient
    // cannot be trusted not to compute one on its own, so a client library is not used here).

    [Fact]
    public async Task VideoRoute_ChunkedBodyBetweenTheAlertCapAndTheVideoCap_PassesTheSizeGate()
    {
        var port = GetFreePort();
        var switchCalls = 0;
        using var server = StartVideoOnly(port, _ => { Interlocked.Increment(ref switchCalls); return true; }, FakeVideoProbes(exists: true));

        var padding = new string('1', AlertHttpProtocol.MaxBodyBytes + 200);
        var body = "{\"warning\":" + padding + "}";
        Assert.True(Encoding.UTF8.GetByteCount(body) > AlertHttpProtocol.MaxBodyBytes);
        Assert.True(Encoding.UTF8.GetByteCount(body) < VideoWallpaperHttpProtocol.MaxBodyBytes);

        var (status, responseBody) = await SendRawAsync(
            port, BuildRawChunkedRequest(VideoWallpaperHttpProtocol.VideoPath, $"127.0.0.1:{port}", body));

        // Passes the video route's wider size gate: it fails validation for an unrelated reason
        // (unknown field), never 413 -- same as the declared-length sibling test above.
        Assert.Equal(400, status);
        Assert.Contains("unknown field 'warning'", responseBody);
        Assert.Equal(0, switchCalls);
    }

    [Fact]
    public async Task VideoRoute_ChunkedBodyOverTheVideoCap_Returns413WithoutCallingTheSwitchDelegate()
    {
        var port = GetFreePort();
        var switchCalls = 0;
        using var server = StartVideoOnly(port, _ => { Interlocked.Increment(ref switchCalls); return true; }, FakeVideoProbes(exists: true));

        var oversized = VideoBody(@"C:\" + new string('a', VideoWallpaperHttpProtocol.MaxBodyBytes) + ".mp4");
        Assert.True(Encoding.UTF8.GetByteCount(oversized) > VideoWallpaperHttpProtocol.MaxBodyBytes);

        var (status, responseBody) = await SendRawAsync(
            port, BuildRawChunkedRequest(VideoWallpaperHttpProtocol.VideoPath, $"127.0.0.1:{port}", oversized));

        Assert.Equal(413, status);
        Assert.Equal(AlertPipeProtocol.FormatError("request body is too large"), responseBody);
        Assert.Equal(0, switchCalls);
    }

    [Fact]
    public async Task AlertsRoute_TheSameChunkedBodyThatPassesOnVideo_Returns413()
    {
        var port = GetFreePort();
        var handlerCalls = 0;
        using var server = Start(port, _ => { Interlocked.Increment(ref handlerCalls); return "ok"; });

        var padding = new string('1', AlertHttpProtocol.MaxBodyBytes + 200);
        var body = "{\"warning\":" + padding + "}";
        Assert.True(Encoding.UTF8.GetByteCount(body) > AlertHttpProtocol.MaxBodyBytes);
        Assert.True(Encoding.UTF8.GetByteCount(body) < VideoWallpaperHttpProtocol.MaxBodyBytes);

        var (status, responseBody) = await SendRawAsync(
            port, BuildRawChunkedRequest(AlertHttpProtocol.AlertsPath, $"127.0.0.1:{port}", body));

        Assert.Equal(413, status);
        Assert.Equal(AlertPipeProtocol.FormatError("request body is too large"), responseBody);
        Assert.Equal(0, handlerCalls);
    }

    // ---- V3: /v1/wallpaper/video, behind the same gates ----

    [Fact]
    public async Task VideoRoute_ValidRequest_CallsSwitchDelegateWithThePath_Returns202()
    {
        var port = GetFreePort();
        string? received = null;
        using var server = StartVideoOnly(port, path => { received = path; return true; }, FakeVideoProbes(exists: true));

        var (status, body) = await PostVideoAsync(port, VideoBody(ExistingVideo));

        Assert.Equal(202, status);
        Assert.Equal("ok", body);
        Assert.Equal(ExistingVideo, received);
    }

    [Fact]
    public async Task VideoRoute_MissingAuthorization_Returns401WithWwwAuthenticate()
    {
        var port = GetFreePort();
        using var server = StartVideoOnly(port, _ => true, FakeVideoProbes(exists: true));

        using var client = NewClientWithoutAuth();
        using var response = await client.PostAsync(
            $"http://127.0.0.1:{port}{VideoWallpaperHttpProtocol.VideoPath}", VideoJsonContent(VideoBody(ExistingVideo)));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, v => v.Scheme == "Bearer");
    }

    [Fact]
    public async Task VideoRoute_WrongToken_Returns401()
    {
        var port = GetFreePort();
        using var server = StartVideoOnly(port, _ => true, FakeVideoProbes(exists: true));

        using var client = NewClientWithoutAuth();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-the-token");
        using var response = await client.PostAsync(
            $"http://127.0.0.1:{port}{VideoWallpaperHttpProtocol.VideoPath}", VideoJsonContent(VideoBody(ExistingVideo)));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task VideoRoute_OriginHeaderPresent_Returns403()
    {
        var port = GetFreePort();
        using var server = StartVideoOnly(port, _ => true, FakeVideoProbes(exists: true));

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"http://127.0.0.1:{port}{VideoWallpaperHttpProtocol.VideoPath}")
        {
            Content = VideoJsonContent(VideoBody(ExistingVideo)),
        };
        request.Headers.Add("Origin", "http://evil.example");

        using var client = NewClient();
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.StartsWith("error: ", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task VideoRoute_ForeignHost_Returns403()
    {
        var port = GetFreePort();
        using var server = StartVideoOnly(port, _ => true, FakeVideoProbes(exists: true));

        var (status, body) = await SendRawAsync(
            port, BuildRawRequest(VideoWallpaperHttpProtocol.VideoPath, "evil.example:1234", VideoBody(ExistingVideo)));

        Assert.Equal(403, status);
        Assert.Equal(AlertPipeProtocol.FormatError("unexpected host header"), body);
    }

    [Fact]
    public async Task VideoRoute_WrongMethod_Returns405WithAllowHeader()
    {
        var port = GetFreePort();
        using var server = StartVideoOnly(port, _ => true, FakeVideoProbes(exists: true));

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"http://127.0.0.1:{port}{VideoWallpaperHttpProtocol.VideoPath}");
        using var client = NewClient();
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Contains("POST", response.Content.Headers.Allow);
    }

    [Fact]
    public async Task VideoRoute_WrongContentType_Returns415()
    {
        var port = GetFreePort();
        using var server = StartVideoOnly(port, _ => true, FakeVideoProbes(exists: true));

        using var client = NewClient();
        var content = new StringContent(VideoBody(ExistingVideo), Encoding.UTF8, "text/plain");
        using var response = await client.PostAsync(
            $"http://127.0.0.1:{port}{VideoWallpaperHttpProtocol.VideoPath}", content);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task VideoRoute_BodyOverTheVideoCap_Returns413()
    {
        var port = GetFreePort();
        var switchCalls = 0;
        using var server = StartVideoOnly(port, _ => { Interlocked.Increment(ref switchCalls); return true; }, FakeVideoProbes(exists: true));

        var oversized = VideoBody(@"C:\" + new string('a', VideoWallpaperHttpProtocol.MaxBodyBytes) + ".mp4");
        Assert.True(Encoding.UTF8.GetByteCount(oversized) > VideoWallpaperHttpProtocol.MaxBodyBytes);

        var (status, _) = await PostVideoAsync(port, oversized);

        Assert.Equal(413, status);
        Assert.Equal(0, switchCalls);
    }

    /// <summary>
    /// The proof that gate 8/9 really does pick its size cap from the matched ROUTE, before a single
    /// byte is read: the exact same oversized-for-alerts body is rejected with 413 on
    /// <see cref="AlertHttpProtocol.AlertsPath"/> (its own 1024-byte cap) but passes the size gate on
    /// <see cref="VideoWallpaperHttpProtocol.VideoPath"/> (its wider 4096-byte cap) on ONE server with
    /// both routes enabled -- it fails validation there for an unrelated reason (an unknown JSON
    /// field), never for size.
    /// </summary>
    [Fact]
    public async Task BodyBetweenTheAlertCapAndTheVideoCap_Is413OnAlertsButNotOnVideo()
    {
        var port = GetFreePort();
        using var server = StartBoth(port, _ => "ok", _ => true, FakeVideoProbes(exists: true));

        var padding = new string('1', AlertHttpProtocol.MaxBodyBytes + 200);
        var body = "{\"warning\":" + padding + "}";
        Assert.True(Encoding.UTF8.GetByteCount(body) > AlertHttpProtocol.MaxBodyBytes);
        Assert.True(Encoding.UTF8.GetByteCount(body) < VideoWallpaperHttpProtocol.MaxBodyBytes);

        var (alertStatus, _) = await PostAsync(port, body);
        var (videoStatus, videoBody) = await PostVideoAsync(port, body);

        Assert.Equal(413, alertStatus);
        Assert.NotEqual(413, videoStatus);
        Assert.Equal(400, videoStatus);
        Assert.Contains("unknown field 'warning'", videoBody);
    }

    [Fact]
    public async Task VideoRoute_MalformedBody_Returns400WithItsOwnMessage()
    {
        var port = GetFreePort();
        using var server = StartVideoOnly(port, _ => true, FakeVideoProbes(exists: true));

        var (status, body) = await PostVideoAsync(port, "not-json");

        Assert.Equal(400, status);
        Assert.Equal(AlertPipeProtocol.FormatError("body is not valid JSON"), body);
    }

    [Fact]
    public async Task VideoRoute_MissingFile_Returns404WithItsOwnMessage()
    {
        var port = GetFreePort();
        using var server = StartVideoOnly(port, _ => true, FakeVideoProbes(exists: false));

        var (status, body) = await PostVideoAsync(port, VideoBody(@"C:\videos\missing.mp4"));

        Assert.Equal(404, status);
        Assert.Equal(AlertPipeProtocol.FormatError("file does not exist"), body);
    }

    [Fact]
    public async Task VideoRoute_WrongExtension_Returns415WithItsOwnMessage()
    {
        var port = GetFreePort();
        using var server = StartVideoOnly(port, _ => true, FakeVideoProbes(exists: true));

        var (status, body) = await PostVideoAsync(port, VideoBody(@"C:\videos\x.txt"));

        Assert.Equal(415, status);
        Assert.Equal(AlertPipeProtocol.FormatError("file must have the .mp4 extension"), body);
    }

    [Fact]
    public async Task VideoRoute_SwitchDelegateReturnsFalse_Returns503WithNotAvailableError()
    {
        var port = GetFreePort();
        using var server = StartVideoOnly(port, _ => false, FakeVideoProbes(exists: true));

        var (status, body) = await PostVideoAsync(port, VideoBody(ExistingVideo));

        Assert.Equal(VideoWallpaperHttpProtocol.NotAvailableStatusCode, status);
        Assert.Equal(AlertPipeProtocol.FormatError(VideoWallpaperHttpProtocol.NotAvailableError), body);
    }

    [Fact]
    public async Task VideoRoute_SwitchDelegateThrows_Returns500AndTheLoopKeepsServing()
    {
        var port = GetFreePort();
        var diagnostics = new List<string>();
        var first = true;
        using var server = new LocalHttpCommandServer(port, Token, handleCommand: null, diagnostics.Add,
            handleVideoWallpaperSwitch: _ =>
            {
                if (first)
                {
                    first = false;
                    throw new InvalidOperationException("boom");
                }

                return true;
            },
            videoWallpaperProbes: FakeVideoProbes(exists: true));
        server.Start();

        var (firstStatus, firstBody) = await PostVideoAsync(port, VideoBody(ExistingVideo));
        Assert.Equal(500, firstStatus);
        Assert.Equal(AlertPipeProtocol.FormatError("internal error"), firstBody);
        Assert.NotEmpty(diagnostics);

        var (secondStatus, secondBody) = await PostVideoAsync(port, VideoBody(ExistingVideo));
        Assert.Equal(202, secondStatus);
        Assert.Equal("ok", secondBody);
    }

    [Fact]
    public async Task VideoRoute_SwitchDelegateThrows_TheDiagnosticNeverCarriesTheExceptionMessage()
    {
        // An IOException's message routinely names the file -- an absolute path under the user's
        // profile, which no trace line may carry. Only the exception's type is reported.
        var port = GetFreePort();
        var diagnostics = new List<string>();
        using var server = new LocalHttpCommandServer(port, Token, handleCommand: null, diagnostics.Add,
            handleVideoWallpaperSwitch: _ => throw new IOException($"Could not open '{ExistingVideo}'."),
            videoWallpaperProbes: FakeVideoProbes(exists: true));
        server.Start();

        var (status, _) = await PostVideoAsync(port, VideoBody(ExistingVideo));

        Assert.Equal(500, status);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains(nameof(IOException), diagnostic);
        Assert.DoesNotContain("Could not open", diagnostic);
    }

    [Fact]
    public async Task VideoRouteDisabled_AnswersExactlyLikeAnUnknownPath()
    {
        var port = GetFreePort();
        using var server = Start(port, _ => "ok"); // alert route only; handleVideoWallpaperSwitch left null

        using var client = NewClient();
        var (unknownStatus, unknownBody) = await GetAgainstUnknownPath(client, port);
        using var videoResponse = await client.PostAsync(
            $"http://127.0.0.1:{port}{VideoWallpaperHttpProtocol.VideoPath}", VideoJsonContent(VideoBody(ExistingVideo)));

        Assert.Equal((HttpStatusCode)unknownStatus, videoResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, videoResponse.StatusCode);
        Assert.Equal(unknownBody, await videoResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AlertRouteDisabled_VideoRouteEnabled_AlertsAnswerExactlyLikeAnUnknownPath()
    {
        var port = GetFreePort();
        using var server = StartVideoOnly(port, _ => true, FakeVideoProbes(exists: true)); // handleCommand left null

        using var client = NewClient();
        var (unknownStatus, unknownBody) = await GetAgainstUnknownPath(client, port);
        using var alertResponse = await client.PostAsync(
            $"http://127.0.0.1:{port}{AlertHttpProtocol.AlertsPath}", JsonContent("{\"warning\":1}"));

        Assert.Equal((HttpStatusCode)unknownStatus, alertResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, alertResponse.StatusCode);
        Assert.Equal(unknownBody, await alertResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task BothRoutesEnabled_WorkIndependentlyOnOneServer()
    {
        var port = GetFreePort();
        string? receivedCommand = null;
        string? receivedVideoPath = null;
        using var server = StartBoth(
            port,
            text => { receivedCommand = text; return "ok"; },
            path => { receivedVideoPath = path; return true; },
            FakeVideoProbes(exists: true));

        var (alertStatus, alertBody) = await PostAsync(port, "{\"warning\":2}");
        var (videoStatus, videoBody) = await PostVideoAsync(port, VideoBody(ExistingVideo));

        Assert.Equal(202, alertStatus);
        Assert.Equal("ok", alertBody);
        Assert.Equal("warning:2", receivedCommand);

        Assert.Equal(202, videoStatus);
        Assert.Equal("ok", videoBody);
        Assert.Equal(ExistingVideo, receivedVideoPath);
    }

    // ---- S2: /v1/wallpaper/scene, behind the same gates ----

    [Fact]
    public async Task SceneRoute_ValidRequest_CallsSwitchDelegateWithTheCanonicalName_Returns202()
    {
        var port = GetFreePort();
        string? received = null;
        using var server = StartSceneOnly(port, name => { received = name; return true; });

        var (status, body) = await PostSceneAsync(port, SceneBody("Idle"));

        Assert.Equal(202, status);
        Assert.Equal("ok", body);
        Assert.Equal("idle", received);
    }

    [Fact]
    public async Task SceneRoute_MissingAuthorization_Returns401WithWwwAuthenticate()
    {
        var port = GetFreePort();
        using var server = StartSceneOnly(port, _ => true);

        using var client = NewClientWithoutAuth();
        using var response = await client.PostAsync(
            $"http://127.0.0.1:{port}{WallpaperSceneHttpProtocol.ScenePath}", SceneJsonContent(SceneBody("idle")));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, v => v.Scheme == "Bearer");
    }

    [Fact]
    public async Task SceneRoute_WrongMethod_Returns405WithAllowHeader()
    {
        var port = GetFreePort();
        using var server = StartSceneOnly(port, _ => true);

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"http://127.0.0.1:{port}{WallpaperSceneHttpProtocol.ScenePath}");
        using var client = NewClient();
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Contains("POST", response.Content.Headers.Allow);
    }

    [Fact]
    public async Task SceneRoute_BodyOverTheSceneCap_Returns413()
    {
        var port = GetFreePort();
        var switchCalls = 0;
        using var server = StartSceneOnly(port, _ => { Interlocked.Increment(ref switchCalls); return true; });

        var oversized = "{\"scene\":\"" + new string('a', WallpaperSceneHttpProtocol.MaxBodyBytes) + "\"}";
        Assert.True(Encoding.UTF8.GetByteCount(oversized) > WallpaperSceneHttpProtocol.MaxBodyBytes);

        var (status, _) = await PostSceneAsync(port, oversized);

        Assert.Equal(413, status);
        Assert.Equal(0, switchCalls);
    }

    [Fact]
    public async Task SceneRoute_MalformedBody_Returns400WithItsOwnMessage()
    {
        var port = GetFreePort();
        using var server = StartSceneOnly(port, _ => true);

        var (status, body) = await PostSceneAsync(port, "not-json");

        Assert.Equal(400, status);
        Assert.Equal(AlertPipeProtocol.FormatError("body is not valid JSON"), body);
    }

    [Fact]
    public async Task SceneRoute_UnknownSceneName_Returns400WithItsOwnMessage()
    {
        var port = GetFreePort();
        using var server = StartSceneOnly(port, _ => true);

        var (status, body) = await PostSceneAsync(port, SceneBody("video"));

        Assert.Equal(400, status);
        Assert.Equal(
            AlertPipeProtocol.FormatError("field 'scene' must be one of: processing, explorer, idle, raphael"),
            body);
    }

    [Fact]
    public async Task SceneRoute_SwitchDelegateReturnsFalse_Returns503WithNotAvailableError()
    {
        var port = GetFreePort();
        using var server = StartSceneOnly(port, _ => false);

        var (status, body) = await PostSceneAsync(port, SceneBody("idle"));

        Assert.Equal(WallpaperSceneHttpProtocol.NotAvailableStatusCode, status);
        Assert.Equal(AlertPipeProtocol.FormatError(WallpaperSceneHttpProtocol.NotAvailableError), body);
    }

    [Fact]
    public async Task SceneRoute_SwitchDelegateThrows_Returns500AndTheLoopKeepsServing()
    {
        var port = GetFreePort();
        var diagnostics = new List<string>();
        var first = true;
        using var server = new LocalHttpCommandServer(port, Token, handleCommand: null, diagnostics.Add,
            handleWallpaperSceneSwitch: _ =>
            {
                if (first)
                {
                    first = false;
                    throw new InvalidOperationException("boom");
                }

                return true;
            });
        server.Start();

        var (firstStatus, firstBody) = await PostSceneAsync(port, SceneBody("idle"));
        Assert.Equal(500, firstStatus);
        Assert.Equal(AlertPipeProtocol.FormatError("internal error"), firstBody);
        Assert.NotEmpty(diagnostics);

        var (secondStatus, secondBody) = await PostSceneAsync(port, SceneBody("idle"));
        Assert.Equal(202, secondStatus);
        Assert.Equal("ok", secondBody);
    }

    [Fact]
    public async Task SceneRouteDisabled_AnswersExactlyLikeAnUnknownPath()
    {
        var port = GetFreePort();
        using var server = Start(port, _ => "ok"); // alert route only; handleWallpaperSceneSwitch left null

        using var client = NewClient();
        var (unknownStatus, unknownBody) = await GetAgainstUnknownPath(client, port);
        using var sceneResponse = await client.PostAsync(
            $"http://127.0.0.1:{port}{WallpaperSceneHttpProtocol.ScenePath}", SceneJsonContent(SceneBody("idle")));

        Assert.Equal((HttpStatusCode)unknownStatus, sceneResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, sceneResponse.StatusCode);
        Assert.Equal(unknownBody, await sceneResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AllThreeRoutesEnabled_WorkIndependentlyOnOneServer()
    {
        var port = GetFreePort();
        string? receivedCommand = null;
        string? receivedVideoPath = null;
        string? receivedScene = null;
        using var server = new LocalHttpCommandServer(port, Token,
            handleCommand: text => { receivedCommand = text; return "ok"; },
            onDiagnostic: msg => output.WriteLine(msg),
            handleVideoWallpaperSwitch: path => { receivedVideoPath = path; return true; },
            videoWallpaperProbes: FakeVideoProbes(exists: true),
            handleWallpaperSceneSwitch: name => { receivedScene = name; return true; });
        server.Start();

        var (alertStatus, alertBody) = await PostAsync(port, "{\"warning\":2}");
        var (videoStatus, videoBody) = await PostVideoAsync(port, VideoBody(ExistingVideo));
        var (sceneStatus, sceneBody) = await PostSceneAsync(port, SceneBody("raphael"));

        Assert.Equal(202, alertStatus);
        Assert.Equal("ok", alertBody);
        Assert.Equal("warning:2", receivedCommand);

        Assert.Equal(202, videoStatus);
        Assert.Equal("ok", videoBody);
        Assert.Equal(ExistingVideo, receivedVideoPath);

        Assert.Equal(202, sceneStatus);
        Assert.Equal("ok", sceneBody);
        Assert.Equal("raphael", receivedScene);
    }

    // ---- helpers ----

    private const string ExistingVideo = @"C:\videos\x.mp4";

    private LocalHttpCommandServer Start(int port, Func<string, string> handleCommand)
    {
        var server = new LocalHttpCommandServer(port, Token, handleCommand, msg => output.WriteLine(msg));
        server.Start();
        return server;
    }

    private LocalHttpCommandServer StartVideoOnly(
        int port, Func<string, bool> handleVideoWallpaperSwitch, VideoWallpaperFileProbes probes)
    {
        var server = new LocalHttpCommandServer(port, Token, handleCommand: null, msg => output.WriteLine(msg),
            handleVideoWallpaperSwitch, probes);
        server.Start();
        return server;
    }

    private LocalHttpCommandServer StartBoth(
        int port,
        Func<string, string> handleCommand,
        Func<string, bool> handleVideoWallpaperSwitch,
        VideoWallpaperFileProbes probes)
    {
        var server = new LocalHttpCommandServer(port, Token, handleCommand, msg => output.WriteLine(msg),
            handleVideoWallpaperSwitch, probes);
        server.Start();
        return server;
    }

    private LocalHttpCommandServer StartSceneOnly(int port, Func<string, bool> handleWallpaperSceneSwitch)
    {
        var server = new LocalHttpCommandServer(port, Token, handleCommand: null, msg => output.WriteLine(msg),
            handleWallpaperSceneSwitch: handleWallpaperSceneSwitch);
        server.Start();
        return server;
    }

    private static string SceneBody(string scene) => System.Text.Json.JsonSerializer.Serialize(new { scene });

    private static StringContent SceneJsonContent(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<(int Status, string Body)> PostSceneAsync(int port, string jsonBody)
    {
        using var client = NewClient();
        using var response = await client.PostAsync(
            $"http://127.0.0.1:{port}{WallpaperSceneHttpProtocol.ScenePath}", SceneJsonContent(jsonBody));
        var text = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, text);
    }

    private static VideoWallpaperFileProbes FakeVideoProbes(bool exists) => new(
        fileExists: _ => exists,
        directoryExists: _ => false,
        isNetworkDrive: _ => false);

    private static string VideoBody(string path) => System.Text.Json.JsonSerializer.Serialize(new { path });

    private static StringContent VideoJsonContent(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<(int Status, string Body)> PostVideoAsync(int port, string jsonBody)
    {
        using var client = NewClient();
        using var response = await client.PostAsync(
            $"http://127.0.0.1:{port}{VideoWallpaperHttpProtocol.VideoPath}", VideoJsonContent(jsonBody));
        var text = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, text);
    }

    /// <summary>
    /// A path no route this server can ever serve owns, used as the "genuinely unknown path"
    /// reference point for the per-route-disablement tests above.
    /// </summary>
    private static async Task<(int Status, string Body)> GetAgainstUnknownPath(HttpClient client, int port)
    {
        using var response = await client.PostAsync($"http://127.0.0.1:{port}/v1/definitely-unknown", JsonContent("{}"));
        var text = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, text);
    }

    private static HttpClient NewClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        client.Timeout = ShortTimeout;
        return client;
    }

    private static HttpClient NewClientWithoutAuth() => new() { Timeout = ShortTimeout };

    private static HttpRequestMessage NewRequest(int port, HttpMethod method, string? body)
    {
        var request = new HttpRequestMessage(method, $"http://127.0.0.1:{port}{AlertHttpProtocol.AlertsPath}");
        if (body is not null)
        {
            request.Content = JsonContent(body);
        }

        return request;
    }

    private static StringContent JsonContent(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<(int Status, string Body)> PostAsync(int port, string jsonBody, string contentType = "application/json")
    {
        using var client = NewClient();
        var content = new StringContent(jsonBody, Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        using var response = await client.PostAsync($"http://127.0.0.1:{port}{AlertHttpProtocol.AlertsPath}", content);
        var text = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, text);
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

    /// <summary>
    /// Builds a raw HTTP/1.1 request with a Content-Length body, byte for byte -- no client library
    /// involved, so nothing can normalise or reject the <paramref name="hostHeader"/> on its way out.
    /// V3: takes an explicit <paramref name="path"/> so the same raw-socket idiom covers both routes.
    /// </summary>
    private static string BuildRawRequest(string path, string hostHeader, string body, string? authorization = null)
    {
        var bodyBytes = Encoding.UTF8.GetByteCount(body);
        return "POST " + path + " HTTP/1.1\r\n" +
            "Host: " + hostHeader + "\r\n" +
            "Content-Type: application/json\r\n" +
            "Authorization: " + (authorization ?? $"Bearer {Token}") + "\r\n" +
            "Content-Length: " + bodyBytes + "\r\n" +
            "Connection: close\r\n\r\n" +
            body;
    }

    /// <summary>
    /// Builds a raw, genuinely chunked HTTP/1.1 request (<c>Transfer-Encoding: chunked</c>, no
    /// <c>Content-Length</c> at all) -- proof that the "unknown length" branch of the body reader is
    /// exercised by real chunked wire framing, not by a client library's own internal buffering. V3
    /// (R3-002): takes an explicit <paramref name="path"/> so the same raw-socket idiom covers both
    /// routes, the same way <see cref="BuildRawRequest"/> already does for the declared-length case.
    /// </summary>
    private static string BuildRawChunkedRequest(string path, string hostHeader, string body)
    {
        var sb = new StringBuilder();
        sb.Append("POST ").Append(path).Append(" HTTP/1.1\r\n");
        sb.Append("Host: ").Append(hostHeader).Append("\r\n");
        sb.Append("Content-Type: application/json\r\n");
        sb.Append("Authorization: Bearer ").Append(Token).Append("\r\n");
        sb.Append("Transfer-Encoding: chunked\r\n");
        sb.Append("Connection: close\r\n\r\n");

        var bytes = Encoding.UTF8.GetBytes(body);
        const int chunkSize = 256;
        for (var offset = 0; offset < bytes.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, bytes.Length - offset);
            sb.Append(length.ToString("x")).Append("\r\n");
            sb.Append(Encoding.UTF8.GetString(bytes, offset, length)).Append("\r\n");
        }

        sb.Append("0\r\n\r\n");
        return sb.ToString();
    }

    /// <summary>
    /// Sends <paramref name="requestText"/> byte for byte over a raw <see cref="TcpClient"/> and
    /// parses the raw response back into a status code and body -- proof of whichever layer actually
    /// answered (http.sys itself, or this class's own <c>HandleRequest</c>), since a client library
    /// never gets a chance to reinterpret anything in between.
    /// </summary>
    private static async Task<(int Status, string Body)> SendRawAsync(int port, string requestText)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var stream = client.GetStream();
        var requestBytes = Encoding.UTF8.GetBytes(requestText);
        await stream.WriteAsync(requestBytes);

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var readTask = reader.ReadToEndAsync();
        var completed = await Task.WhenAny(readTask, Task.Delay(ShortTimeout));
        if (completed != readTask)
        {
            throw new TimeoutException($"No response within {ShortTimeout}.");
        }

        var raw = await readTask;
        var statusLine = raw[..raw.IndexOf("\r\n", StringComparison.Ordinal)];
        var status = int.Parse(statusLine.Split(' ')[1]);

        var bodyIndex = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var body = bodyIndex >= 0 ? raw[(bodyIndex + 4)..] : string.Empty;
        return (status, body);
    }
}

/// <summary>
/// Skips <see cref="LocalHttpCommandServerTests.ConnectingThroughANonLoopbackAddress_IsRejectedOrRefused"/>
/// when this machine has no non-loopback IPv4 address to connect through. xunit 2 cannot skip a fact
/// from inside its body -- the same reason <c>RequiresDesktopFactAttribute</c> probes its own
/// environment gate in the constructor rather than the test method -- so the probe runs once, at
/// discovery time, here.
/// </summary>
internal sealed class RequiresNonLoopbackIPv4FactAttribute : FactAttribute
{
    public static IPAddress? NonLoopbackIPv4 { get; } = FindNonLoopbackIPv4();

    public RequiresNonLoopbackIPv4FactAttribute()
    {
        if (NonLoopbackIPv4 is null)
        {
            Skip = "No non-loopback IPv4 address is configured on this machine.";
        }
    }

    private static IPAddress? FindNonLoopbackIPv4()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up)
                .SelectMany(ni => ni.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
        }
        catch
        {
            return null;
        }
    }
}
