using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace CosmicWin.Interop;

/// <summary>
/// The other real <see cref="IAlertCommandServer"/> (feature http-alert-endpoint, H2): a
/// loopback-only <see cref="HttpListener"/> that runs every accepted request through the exact same
/// <c>Func&lt;string,string&gt;</c> handler the pipe (<see
/// cref="Win32.NamedPipeAlertCommandServer"/>) uses, so a caller on this PC that would rather speak
/// HTTP than a named pipe gets identical behaviour from the shared alert grammar.
/// </summary>
/// <remarks>
/// <para>
/// Bound to two literal (never wildcard) prefixes: <c>http://127.0.0.1:{port}/</c> and
/// <c>http://localhost:{port}/</c> -- measured 2026-09-24: both start fine WITHOUT elevation (no
/// urlacl reservation needed), unlike a wildcard <c>+</c> or <c>*</c> prefix, which
/// <c>HttpListener</c> refuses to register for a non-admin process. Both are needed for a mundane
/// reason, not a security one: measured 2026-09-25, a caller that types the literal URL
/// <c>http://localhost:{port}/...</c> resolves "localhost" through DNS first, and on this machine
/// that resolves to the IPv6 loopback address -- a listener bound only to the IPv4 literal
/// <c>127.0.0.1</c> prefix never even receives that connection at the socket level (it just times
/// out). If registering both together ever fails (e.g. IPv6 disabled on some machine), <see
/// cref="Start"/> falls back to the IPv4 literal alone rather than refusing to start at all.
/// </para>
/// <para>
/// MEASURED 2026-09-25 (raw <see cref="System.Net.Sockets.TcpClient"/> requests, no client library
/// involved -- see <c>HttpAlertCommandServerTests</c>'s "R3-001/002" region): for a connection that
/// arrives on one of THIS class's own registered local addresses (i.e. loopback, the only kind it
/// ever binds), http.sys performs NO <c>Host</c>-header filtering of its own -- it forwards the
/// request to this listener regardless of what <c>Host</c> says. <c>Host: localhost:{port}</c>,
/// <c>Host: 127.0.0.1:{port}</c>, and an arbitrary <c>Host: evil.example:1234</c> all reached <see
/// cref="HandleRequest"/> unmodified and got back whatever THIS class decided. So the <c>Host</c>
/// check in <see cref="HandleRequest"/> (gate step 3) is not defense in depth on top of an http.sys
/// filter for THAT case -- it is the entire defense against DNS rebinding from a loopback client.
/// Separately, ALSO measured 2026-09-25: once two literal hostname prefixes share a port (127.0.0.1
/// and localhost), http.sys DOES validate a connection that arrives on neither registered local
/// address at all -- e.g. a LAN NIC -- and turns it away itself with its own
/// "400 Bad Request - Invalid Hostname" page before <see cref="HandleRequest"/> ever runs. Binding
/// only loopback prefixes is what makes that http.sys-level rejection possible in the first place;
/// the <see cref="HttpListenerRequest.RemoteEndPoint"/> check (gate step 1, extracted as the pure,
/// unit-tested <see cref="IsLoopbackRemote"/>) is this class's OWN, independent line of defense for
/// the case a connection reaches here anyway -- e.g. a dual-stack socket handing back an address
/// shape this class did not expect -- not a bet on http.sys always doing that filtering for it.
/// </para>
/// <para>
/// One background thread, calling the synchronous, blocking <see cref="HttpListener.GetContext"/> in
/// a loop -- the same "one connection at a time, never throws the loop down" shape as <see
/// cref="Win32.NamedPipeAlertCommandServer.RunLoop"/>. <see cref="Dispose"/> calling <see
/// cref="HttpListener.Stop"/> is what makes the blocked <c>GetContext</c> call return (with an
/// exception caught by the <c>!listener.IsListening</c> branch below), the same way cancelling the
/// pipe's own <c>_stopping</c> token unblocks its pending <c>WaitForConnectionAsync</c>. A repeating,
/// non-shutdown <c>GetContext</c> failure backs off exactly like
/// <see cref="Win32.NamedPipeAlertCommandServer.RunLoop"/> does, though no black-box test can
/// currently force that path open (see <c>HttpAlertCommandServerTests</c>'s "R3-005" note).
/// </para>
/// <para>
/// MEASURED 2026-09-25 on hardware (bug found during the H5 manual check): a plain Winsock <see
/// cref="System.Net.Sockets.TcpListener"/> already bound to the port made <see
/// cref="HttpListener.Start"/> succeed WITHOUT throwing -- http.sys registered the URL prefixes, the
/// diagnostic said the port was listening, and every request then hung forever (the squatter
/// accepted the TCP connection at the socket level and simply never answered it; http.sys itself
/// never saw the request). After the squatter exited, requests got connection refused: http.sys
/// never quietly re-bound on its own. So a successful, non-throwing <see cref="HttpListener.Start"/>
/// is NOT proof this instance can actually be reached -- <see cref="Start"/> below always follows it
/// with a background reachability self-probe (a real HTTP request to its own <see
/// cref="AlertHttpProtocol.AlertsPath"/>, answered by http.sys/this listener only if something is
/// really listening behind it) and reports exactly one <c>reachable</c>/<c>NOT reachable</c>
/// diagnostic once that resolves. An unreachable result stops and closes the listener so this
/// instance goes -- and stays -- inert rather than holding a dead registration open; <see
/// cref="Dispose"/> afterwards is still safe. Attempts to reproduce the underlying "<see
/// cref="HttpListener.Start"/> silently succeeds over an already-squatted port" quirk from a test in
/// this repository's environment -- including from a genuinely separate process -- instead always
/// throw immediately (the existing, already-tested "port already in use" path); the self-probe
/// mechanism is therefore covered here through an internal, test-only probe seam
/// (<c>HttpAlertCommandServerTests</c>'s "H5b" region) rather than a literal black-box squatter race,
/// the same documented-gap idiom "R3-005" above already uses.
/// </para>
/// </remarks>
public sealed class HttpAlertCommandServer : IAlertCommandServer
{
    /// <summary>
    /// Bounds every per-request wait (<see cref="HttpListenerTimeoutManager"/>) so a slow or silent
    /// client cannot hold the single-threaded loop open indefinitely -- the HTTP analogue of <see
    /// cref="Win32.NamedPipeAlertCommandServer.ConnectionReadTimeout"/>.
    /// </summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Starting delay before <see cref="RunLoop"/> retries a repeating, non-shutdown
    /// <see cref="HttpListener.GetContext"/> failure, doubled up to <see cref="MaxRetryBackoff"/> and
    /// reset the moment a context is actually accepted -- the HTTP analogue of
    /// <see cref="Win32.NamedPipeAlertCommandServer.InitialRetryBackoff"/>.
    /// </summary>
    public static readonly TimeSpan InitialRetryBackoff = TimeSpan.FromMilliseconds(100);

    /// <summary>Ceiling for the backoff above, matching <see cref="Win32.NamedPipeAlertCommandServer.MaxRetryBackoff"/>.</summary>
    public static readonly TimeSpan MaxRetryBackoff = TimeSpan.FromMilliseconds(500);

    /// <summary>Bound on <see cref="Dispose"/> joining the background thread.</summary>
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Bounds the post-start reachability self-probe's own HTTP request (see the class remarks and
    /// <see cref="VerifyReachabilityAsync"/>) -- generous enough for a loopback round trip, short
    /// enough that a squatted port that never answers is reported quickly.
    /// </summary>
    public static readonly TimeSpan SelfProbeTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Sent on the self-probe's own GET request so <see cref="HandleRequest"/> can recognise it and
    /// skip the ordinary "rejected" diagnostic for the 405 it deliberately provokes (gate step 5) --
    /// success for the probe, not an anomaly worth alarming an operator over. The header carries no
    /// authority: it never lets a request skip an earlier gate step (loopback/Origin/Host/path), it
    /// only silences one diagnostic on the one gate step the probe itself is designed to hit.
    /// </summary>
    internal const string SelfProbeHeaderName = "X-CosmicWin-Self-Probe";

    private const string SelfProbeHeaderValue = "1";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly int _port;
    private readonly string _token;
    private readonly byte[] _tokenBytes;
    private readonly Func<string, string> _handleCommand;
    private readonly Action<string> _onDiagnostic;
    private readonly Func<int, Task<SelfProbeOutcome>>? _selfProbeOverride;
    private readonly CancellationTokenSource _stopping = new();

    private HttpListener? _listener;
    private Thread? _thread;
    private bool _disposed;

    /// <param name="port">The loopback TCP port to listen on, 1-65535.</param>
    /// <param name="token">
    /// The already-loaded bearer token every request must present. Loading/creating it is H3's job;
    /// this type only compares against whatever it is handed.
    /// </param>
    /// <param name="handleCommand">
    /// The SAME delegate the pipe uses. Never called for a request the gate already rejected. A
    /// throw from it is caught and reported as <c>"error: internal error"</c>, same as the pipe.
    /// </param>
    /// <param name="onDiagnostic">
    /// Told about every start failure, rejection and per-request error. Defaults to a no-op, the
    /// same convention <see cref="Win32.NamedPipeAlertCommandServer"/> uses. Never told the token or
    /// the raw <c>Authorization</c> header.
    /// </param>
    public HttpAlertCommandServer(int port, string token, Func<string, string> handleCommand, Action<string>? onDiagnostic = null)
        : this(port, token, handleCommand, onDiagnostic, selfProbeOverride: null)
    {
    }

    /// <summary>
    /// Test-only seam (see the class remarks' "H5b" note): lets a test replace the real HTTP
    /// self-probe with a deterministic stub, since the OS-level condition it defends against --
    /// <see cref="HttpListener.Start"/> succeeding over a port something else already occupies --
    /// could not be reproduced from a black-box test in this repository's environment. Production
    /// code always uses the public constructor above, which leaves this <see langword="null"/> and
    /// so always runs the real <see cref="DefaultSelfProbeAsync"/>.
    /// </summary>
    internal HttpAlertCommandServer(
        int port, string token, Func<string, string> handleCommand, Action<string>? onDiagnostic,
        Func<int, Task<SelfProbeOutcome>>? selfProbeOverride)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "The port must be between 1 and 65535.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        _port = port;
        _token = token;
        _tokenBytes = Encoding.UTF8.GetBytes(token);
        _handleCommand = handleCommand ?? throw new ArgumentNullException(nameof(handleCommand));
        _onDiagnostic = onDiagnostic ?? (_ => { });
        _selfProbeOverride = selfProbeOverride;
    }

    /// <summary>
    /// Starts listening on a background thread. Idempotent: a second call while already listening is
    /// a no-op, and so is any call after <see cref="Dispose"/> (R3-004: a disposed instance stays
    /// disposed -- it never quietly re-binds the port). Never throws: a failure to bind the port
    /// (e.g. already in use) is reported through <see cref="_onDiagnostic"/> and this instance stays
    /// inert -- the pipe keeps working either way, the same contract <see cref="IAlertCommandServer"/>
    /// documents.
    /// </summary>
    public void Start()
    {
        if (_disposed || _thread is not null)
        {
            return;
        }

        var listener = TryCreateListener(includeLocalhostPrefix: true)
            ?? TryCreateListener(includeLocalhostPrefix: false);

        if (listener is null)
        {
            return;
        }

        _listener = listener;
        _thread = new Thread(() => RunLoop(listener)) { IsBackground = true, Name = "CosmicWin.AlertHttp" };
        _thread.Start();

        // Fire-and-forget, deliberately: Start() itself must stay fast and never throw (see its own
        // doc comment). The probe's own diagnostic, once it resolves, is the caller-visible result.
        _ = Task.Run(() => VerifyReachabilityAsync(listener));
    }

    /// <summary>
    /// Runs once per successful <see cref="Start"/>, off the caller's thread: see the class remarks'
    /// "H5b" note for why a listener that started without throwing is not by itself proof anything is
    /// actually listening behind it. Reports exactly one diagnostic and, on failure, stops and closes
    /// <paramref name="listener"/> so this instance goes -- and stays -- inert instead of holding a
    /// dead registration open. Deliberately does NOT set <see cref="_disposed"/>: a real,
    /// caller-initiated <see cref="Dispose"/> -- before, during or after this runs -- must still be
    /// able to run its own full, symmetric teardown (join the thread, dispose <see cref="_stopping"/>)
    /// rather than short-circuit on a flag this method set for an unrelated reason.
    /// </summary>
    private async Task VerifyReachabilityAsync(HttpListener listener)
    {
        var probe = _selfProbeOverride ?? DefaultSelfProbeAsync;
        SelfProbeOutcome outcome;
        try
        {
            outcome = await probe(_port).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            outcome = SelfProbeOutcome.Unreachable($"{error.GetType().Name}: {error.Message}");
        }

        if (_disposed)
        {
            // A real Dispose already ran (or is racing this): nothing left to report or clean up.
            return;
        }

        if (outcome.Reachable)
        {
            _onDiagnostic($"alert http: reachable on port {_port}");
            return;
        }

        _onDiagnostic($"alert http: NOT reachable on port {_port} ({outcome.Detail}); another program may be using the port. The named pipe still works.");

        try
        {
            listener.Stop();
        }
        catch
        {
            // Best-effort: the goal is only to make sure nothing keeps GetContext blocked forever.
        }

        try
        {
            listener.Close();
        }
        catch
        {
            // Best-effort cleanup on the way out.
        }
    }

    /// <summary>
    /// The real self-probe: a plain GET to this server's own <see
    /// cref="AlertHttpProtocol.AlertsPath"/>, carrying <see cref="SelfProbeHeaderName"/> so <see
    /// cref="HandleRequest"/> recognises it and skips the ordinary "rejected" diagnostic for the 405
    /// it deliberately provokes (gate step 5, reached ahead of the bearer-token check -- this probe
    /// carries no token and needs none). Reachable only if OUR listener answered with OUR exact
    /// method-not-allowed body -- a squatter that merely accepted the TCP connection and never
    /// answered at all times out instead, and anything else answering (a different server entirely)
    /// would not match the exact body either.
    /// </summary>
    private static async Task<SelfProbeOutcome> DefaultSelfProbeAsync(int port)
    {
        using var client = new HttpClient { Timeout = SelfProbeTimeout };
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}{AlertHttpProtocol.AlertsPath}");
        request.Headers.TryAddWithoutValidation(SelfProbeHeaderName, SelfProbeHeaderValue);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return SelfProbeOutcome.Unreachable($"{error.GetType().Name}: {error.Message}");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var expected = AlertPipeProtocol.FormatError("method not allowed");
            if (response.StatusCode == HttpStatusCode.MethodNotAllowed && string.Equals(body, expected, StringComparison.Ordinal))
            {
                return SelfProbeOutcome.Success();
            }

            return SelfProbeOutcome.Unreachable($"unexpected reply {(int)response.StatusCode} {response.StatusCode}");
        }
    }

    /// <summary>
    /// Builds and starts one <see cref="HttpListener"/> attempt. <see langword="null"/> on failure,
    /// with a diagnostic already reported -- the caller decides whether to retry with a narrower set
    /// of prefixes or give up for good.
    /// </summary>
    private HttpListener? TryCreateListener(bool includeLocalhostPrefix)
    {
        HttpListener? listener = null;
        try
        {
            listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
            if (includeLocalhostPrefix)
            {
                listener.Prefixes.Add($"http://localhost:{_port}/");
            }

            listener.TimeoutManager.EntityBody = RequestTimeout;
            listener.TimeoutManager.HeaderWait = RequestTimeout;
            listener.TimeoutManager.IdleConnection = RequestTimeout;
            listener.TimeoutManager.DrainEntityBody = RequestTimeout;
            listener.Start();
            return listener;
        }
        catch (Exception error)
        {
            var prefixes = includeLocalhostPrefix ? "127.0.0.1 and localhost" : "127.0.0.1";
            _onDiagnostic($"alert http: failed to start listening on port {_port} ({prefixes}): {error.GetType().Name}: {error.Message}");

            // Finding R3-102: a failed attempt must not linger until finalization, and the fallback
            // attempt that may follow creates a second listener on the same port.
            try
            {
                listener?.Close();
            }
            catch
            {
                // Best-effort: the attempt already failed and was reported.
            }

            return null;
        }
    }

    /// <summary>
    /// Runs until <see cref="Dispose"/> stops <paramref name="listener"/>, which is what makes the
    /// blocking <see cref="HttpListener.GetContext"/> call below return with an exception instead of
    /// waiting forever. One request at a time, by construction: the next <c>GetContext</c> call is
    /// not made until <see cref="HandleRequest"/> has fully replied to the previous one. A single
    /// request's failure is caught and reported, never allowed to end the loop.
    /// </summary>
    private void RunLoop(HttpListener listener)
    {
        var backoff = InitialRetryBackoff;

        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = listener.GetContext();
                backoff = InitialRetryBackoff; // reset the moment a context is actually accepted
            }
            catch (Exception) when (!listener.IsListening)
            {
                // Dispose already stopped/closed the listener -- an ordinary shutdown, not a fact
                // worth a diagnostic.
                return;
            }
            catch (Exception error)
            {
                _onDiagnostic($"alert http: {error.GetType().Name}: {error.Message}");
                _stopping.Token.WaitHandle.WaitOne(backoff);
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxRetryBackoff.Ticks));
                continue;
            }

            try
            {
                HandleRequest(context);
            }
            catch (Exception error)
            {
                _onDiagnostic($"alert http: {error.GetType().Name}: {error.Message}");
                try
                {
                    context.Response.Close();
                }
                catch
                {
                    // Best-effort: the connection is being abandoned anyway.
                }
            }
        }
    }

    /// <summary>
    /// The request gate, run in the exact order the task file requires -- first failing check wins.
    /// </summary>
    private void HandleRequest(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        // 1. Loopback RemoteEndPoint -- see IsLoopbackRemote and the class remarks for exactly what
        // this does and does not add on top of binding only loopback prefixes.
        if (!IsLoopbackRemote(request.RemoteEndPoint))
        {
            Reject(response, 403, "remote endpoint is not loopback");
            return;
        }

        // 2. Any Origin header at all means a browser sent this.
        if (!string.IsNullOrEmpty(request.Headers["Origin"]))
        {
            Reject(response, 403, "browser requests are not accepted");
            return;
        }

        // 3. Host header must be exactly 127.0.0.1:port or localhost:port (DNS rebinding).
        if (!IsAllowedHost(request.UserHostName))
        {
            Reject(response, 403, "unexpected host header");
            return;
        }

        // 4. Path.
        if (!string.Equals(request.Url?.AbsolutePath, AlertHttpProtocol.AlertsPath, StringComparison.Ordinal))
        {
            Reject(response, 404, "no such route");
            return;
        }

        // 5. Method. OPTIONS (a CORS preflight) is rejected here too, and never answered with any
        // Access-Control-* header. A request carrying the self-probe header (see
        // VerifyReachabilityAsync/DefaultSelfProbeAsync) only ever reaches THIS branch by design --
        // still having passed every gate step ahead of it unmodified -- and gets the identical 405
        // reply, just without the operator-facing "rejected" diagnostic: it is the probe succeeding,
        // not an anomaly.
        if (!string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
        {
            response.Headers["Allow"] = "POST";
            var isSelfProbe = string.Equals(request.Headers[SelfProbeHeaderName], SelfProbeHeaderValue, StringComparison.Ordinal);
            Reject(response, 405, "method not allowed", suppressDiagnostic: isSelfProbe);
            return;
        }

        // 6. Bearer token, constant-time.
        if (!HasValidToken(request))
        {
            response.Headers["WWW-Authenticate"] = "Bearer";
            Reject(response, 401, "missing or invalid bearer token");
            return;
        }

        // 7. Content-Type, ignoring parameters like ";charset=utf-8".
        if (!IsJsonMediaType(request.ContentType))
        {
            Reject(response, 415, "content type must be application/json");
            return;
        }

        // 8/9. Body size cap and UTF-8 validity.
        if (!TryReadBody(request, out var body, out var readStatus, out var readReason))
        {
            Reject(response, readStatus, readReason!);
            return;
        }

        // 10. Translate to the pipe's command grammar.
        if (!AlertHttpProtocol.TryTranslate(body, out var command, out var translateError))
        {
            Reject(response, 400, translateError!);
            return;
        }

        // 11. The same handler the pipe uses.
        string reply;
        try
        {
            reply = _handleCommand(command!);
        }
        catch (Exception error)
        {
            _onDiagnostic($"alert http: the command handler threw {error.GetType().Name}: {error.Message}");
            WriteReply(response, 500, AlertPipeProtocol.FormatError("internal error"));
            return;
        }

        WriteReply(response, AlertHttpProtocol.StatusCodeFor(reply), reply);
    }

    /// <summary>
    /// The pure loopback test behind gate step 1 (R3-003: extracted so it can be unit-tested
    /// deterministically, without a real socket). <see langword="null"/> -- no remote endpoint at all
    /// -- is never loopback. Recognises IPv4 127.0.0.0/8, IPv6 <c>::1</c>, and an IPv4 address mapped
    /// into IPv6 (<c>::ffff:127.x.x.x</c>) -- the shape a dual-stack socket can hand back for an IPv4
    /// peer even though this listener's own prefixes are literal IPv4/hostname, never <c>::</c> or
    /// <c>+</c>.
    /// </summary>
    internal static bool IsLoopbackRemote(IPEndPoint? remote)
    {
        if (remote is null)
        {
            return false;
        }

        var address = remote.Address;
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return IPAddress.IsLoopback(address);
    }

    private bool IsAllowedHost(string? host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        return string.Equals(host, $"127.0.0.1:{_port}", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, $"localhost:{_port}", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsJsonMediaType(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
        {
            return false;
        }

        var mediaType = contentType.Split(';')[0].Trim();
        return string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The <c>Bearer</c> scheme name is matched case-insensitively (R3-006: RFC 7235's <c>auth-scheme</c>
    /// is explicitly case-insensitive, and real clients disagree on casing) -- but the token itself
    /// is compared with <see cref="CryptographicOperations.FixedTimeEquals"/> over UTF-8 bytes,
    /// case-SENSITIVE and constant-time, per the task file. <c>FixedTimeEquals</c> returns
    /// <see langword="false"/> immediately when the lengths differ (a documented, deliberate
    /// short-circuit -- a length mismatch is not a secret worth constant time over).
    /// </summary>
    private bool HasValidToken(HttpListenerRequest request)
    {
        var header = request.Headers["Authorization"];
        if (string.IsNullOrEmpty(header))
        {
            return false;
        }

        var spaceIndex = header.IndexOf(' ');
        if (spaceIndex < 0)
        {
            return false;
        }

        var scheme = header[..spaceIndex];
        if (!string.Equals(scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var providedBytes = Encoding.UTF8.GetBytes(header[(spaceIndex + 1)..]);
        return CryptographicOperations.FixedTimeEquals(providedBytes, _tokenBytes);
    }

    /// <summary>
    /// Reads the body, enforcing <see cref="AlertHttpProtocol.MaxBodyBytes"/> two ways: a DECLARED
    /// <see cref="HttpListenerRequest.ContentLength64"/> past the cap is rejected before a single
    /// byte is read; an UNKNOWN length (chunked transfer) is read at most one byte past the cap, so
    /// the excess is detected without buffering an unbounded stream. Only once the size is known-good
    /// is the buffer decoded as strict UTF-8.
    /// </summary>
    private static bool TryReadBody(HttpListenerRequest request, out string? body, out int status, out string? reason)
    {
        body = null;

        if (request.ContentLength64 > AlertHttpProtocol.MaxBodyBytes)
        {
            status = 413;
            reason = "request body is too large";
            return false;
        }

        byte[] buffer;
        using (var memory = new MemoryStream())
        {
            var chunk = new byte[4096];
            int read;
            var total = 0;
            var overLimit = false;

            while ((read = request.InputStream.Read(chunk, 0, chunk.Length)) > 0)
            {
                total += read;
                if (total > AlertHttpProtocol.MaxBodyBytes)
                {
                    // Enough to know it is oversized; stop reading rather than draining an
                    // arbitrarily large stream.
                    overLimit = true;
                    break;
                }

                memory.Write(chunk, 0, read);
            }

            if (overLimit)
            {
                status = 413;
                reason = "request body is too large";
                return false;
            }

            buffer = memory.ToArray();
        }

        try
        {
            body = StrictUtf8.GetString(buffer);
        }
        catch (DecoderFallbackException)
        {
            status = 400;
            reason = "body is not valid UTF-8";
            return false;
        }

        status = 200;
        reason = null;
        return true;
    }

    private void Reject(HttpListenerResponse response, int status, string reason, bool suppressDiagnostic = false)
    {
        if (!suppressDiagnostic)
        {
            _onDiagnostic($"alert-http rejected {status} {reason}");
        }

        WriteReply(response, status, AlertPipeProtocol.FormatError(reason));
    }

    private static void WriteReply(HttpListenerResponse response, int status, string body)
    {
        try
        {
            response.StatusCode = status;
            response.ContentType = "text/plain; charset=utf-8";
            var bytes = Encoding.UTF8.GetBytes(body);
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
        }
        finally
        {
            response.OutputStream.Close();
        }
    }

    /// <summary>
    /// Stops and closes the listener and joins the background thread. Idempotent, and safe to call
    /// before <see cref="Start"/> was ever called.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping.Cancel(); // wakes RunLoop promptly even mid-backoff-wait

        try
        {
            _listener?.Stop();
        }
        catch
        {
            // Best-effort: the goal below is only to make the blocked GetContext call return.
        }

        var threadStopped = _thread?.Join(JoinTimeout) ?? true;

        try
        {
            _listener?.Close();
        }
        catch
        {
            // Best-effort cleanup on the way out.
        }

        // Finding R3-101: if the bounded join timed out, RunLoop may still reach its backoff wait,
        // which reads _stopping.Token -- disposing the source under it would throw on a background
        // thread and take the process down. Leaving one cancelled source to the GC is harmless.
        if (threadStopped)
        {
            _stopping.Dispose();
        }
    }
}

/// <summary>
/// The result of one reachability self-probe (see <see cref="HttpAlertCommandServer.VerifyReachabilityAsync"/>):
/// either reachable, or not with a short, diagnostic-only <see cref="Detail"/> -- never the bearer
/// token or any other request content.
/// </summary>
internal readonly record struct SelfProbeOutcome(bool Reachable, string? Detail)
{
    public static SelfProbeOutcome Success() => new(true, null);

    public static SelfProbeOutcome Unreachable(string detail) => new(false, detail);
}
