using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using CosmicWin.Interop;
using Xunit.Abstractions;

namespace CosmicWin.Interop.Tests;

/// <summary>
/// Integration coverage for <see cref="HttpAlertCommandServer"/>: a real loopback
/// <see cref="System.Net.HttpListener"/>, a real <see cref="HttpClient"/>, no fakes -- the same
/// "integration: real transport, in-process server" idiom
/// <c>NamedPipeAlertCommandServerTests</c> uses for the pipe half of this feature. Every fact gets
/// its own free loopback port (<see cref="GetFreePort"/>) even though <c>TestParallelism.cs</c>
/// already serialises this assembly, for the same belt-and-braces reason the pipe tests give every
/// fact its own pipe name: a leftover listener from a previous run must never collide with this one.
/// </summary>
public sealed class HttpAlertCommandServerTests(ITestOutputHelper output)
{
    private const string Token = "test-token-abc123";
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);

    // ---- constructor validation ----

    [Fact]
    public void Constructor_PortZero_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new HttpAlertCommandServer(0, Token, _ => "ok"));

    [Fact]
    public void Constructor_PortTooLarge_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new HttpAlertCommandServer(65536, Token, _ => "ok"));

    [Fact]
    public void Constructor_NullToken_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new HttpAlertCommandServer(GetFreePort(), null!, _ => "ok"));

    [Fact]
    public void Constructor_BlankToken_Throws() =>
        Assert.Throws<ArgumentException>(() => new HttpAlertCommandServer(GetFreePort(), "   ", _ => "ok"));

    [Fact]
    public void Constructor_NullHandler_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new HttpAlertCommandServer(GetFreePort(), Token, null!));

    // ---- lifecycle ----

    [Fact]
    public async Task Start_Idempotent_SecondCallStillServesNormally()
    {
        var port = GetFreePort();
        using var server = new HttpAlertCommandServer(port, Token, _ => "ok");
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
        using var occupant = new HttpAlertCommandServer(port, Token, _ => "ok");
        occupant.Start();

        var diagnostics = new List<string>();
        using var server = new HttpAlertCommandServer(port, Token, _ => "ok", diagnostics.Add);

        var exception = Record.Exception(server.Start);

        Assert.Null(exception);
        Assert.NotEmpty(diagnostics);
    }

    [Fact]
    public void Dispose_BeforeStart_IsSafe()
    {
        var server = new HttpAlertCommandServer(GetFreePort(), Token, _ => "ok");
        var exception = Record.Exception(server.Dispose);
        Assert.Null(exception);
    }

    [Fact]
    public void Dispose_Idempotent()
    {
        var server = new HttpAlertCommandServer(GetFreePort(), Token, _ => "ok");
        server.Start();
        server.Dispose();
        var exception = Record.Exception(server.Dispose);
        Assert.Null(exception);
    }

    [Fact]
    public async Task Dispose_StopsAccepting()
    {
        var port = GetFreePort();
        var server = new HttpAlertCommandServer(port, Token, _ => "ok");
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

    [Fact]
    public async Task HostHeaderWrong_Returns403()
    {
        var port = GetFreePort();
        using var server = Start(port, _ => "ok");

        using var request = NewRequest(port, HttpMethod.Post, "{\"warning\":1}");
        request.Headers.Host = "evil.example:1234";

        using var client = NewClient();
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
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
    public async Task HandlerReplyQueueFull_PassesThroughAs429()
    {
        var port = GetFreePort();
        using var server = Start(port, _ => AlertPipeProtocol.QueueFullReply);

        var (status, body) = await PostAsync(port, "{\"warning\":1}");

        Assert.Equal(429, status);
        Assert.Equal(AlertPipeProtocol.QueueFullReply, body);
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
        using var server = new HttpAlertCommandServer(port, Token, _ =>
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
            // The listener is bound only to the literal 127.0.0.1 prefix, so if a connection was
            // even accepted through the other interface, the RemoteEndPoint check must still 403 it.
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            // Also acceptable: http.sys never routed the connection to this listener at all,
            // because it is bound to the literal loopback address, not a wildcard prefix.
            output.WriteLine($"Connection through {address} was refused/unreachable: {error.Message}");
        }
    }

    // ---- helpers ----

    private HttpAlertCommandServer Start(int port, Func<string, string> handleCommand)
    {
        var server = new HttpAlertCommandServer(port, Token, handleCommand, msg => output.WriteLine(msg));
        server.Start();
        return server;
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
}

/// <summary>
/// Skips <see cref="HttpAlertCommandServerTests.ConnectingThroughANonLoopbackAddress_IsRejectedOrRefused"/>
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
