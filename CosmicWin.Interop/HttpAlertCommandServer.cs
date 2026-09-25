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
/// Bound to the single, literal prefix <c>http://127.0.0.1:{port}/</c> -- measured 2026-09-24: this
/// starts fine WITHOUT elevation (no urlacl reservation needed), unlike a wildcard <c>+</c> or
/// <c>*</c> prefix, which <c>HttpListener</c> refuses to register for a non-admin process. Binding to
/// the literal loopback address is also what keeps a client on another NIC from ever reaching this
/// listener at the socket level in the first place; the <see
/// cref="HttpListenerRequest.RemoteEndPoint"/> check below is defense in depth on top of that, not
/// instead of it, because http.sys matches a request to whichever registered prefix's HOST header it
/// declares, not the interface the packet actually arrived on.
/// </para>
/// <para>
/// One background thread, calling the synchronous, blocking <see cref="HttpListener.GetContext"/> in
/// a loop -- the same "one connection at a time, never throws the loop down" shape as <see
/// cref="Win32.NamedPipeAlertCommandServer.RunLoop"/>. <see cref="Dispose"/> calling <see
/// cref="HttpListener.Stop"/> is what makes the blocked <c>GetContext</c> call return (with an
/// <see cref="HttpListenerException"/>), the same way cancelling <c>_stopping</c> unblocks the pipe's
/// pending <c>WaitForConnectionAsync</c>.
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

    /// <summary>Bound on <see cref="Dispose"/> joining the background thread.</summary>
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(5);

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly int _port;
    private readonly string _token;
    private readonly byte[] _tokenBytes;
    private readonly Func<string, string> _handleCommand;
    private readonly Action<string> _onDiagnostic;

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
    }

    /// <summary>
    /// Starts listening on a background thread. Idempotent: a second call while already listening is
    /// a no-op. Never throws: a failure to bind the port (e.g. already in use) is reported through
    /// <see cref="_onDiagnostic"/> and this instance stays inert -- the pipe keeps working either
    /// way, the same contract <see cref="IAlertCommandServer"/> documents.
    /// </summary>
    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        HttpListener listener;
        try
        {
            listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
            listener.TimeoutManager.EntityBody = RequestTimeout;
            listener.TimeoutManager.HeaderWait = RequestTimeout;
            listener.TimeoutManager.IdleConnection = RequestTimeout;
            listener.TimeoutManager.DrainEntityBody = RequestTimeout;
            listener.Start();
        }
        catch (Exception error)
        {
            _onDiagnostic($"alert http: failed to start listening on port {_port}: {error.GetType().Name}: {error.Message}");
            return;
        }

        _listener = listener;
        _thread = new Thread(() => RunLoop(listener)) { IsBackground = true, Name = "CosmicWin.AlertHttp" };
        _thread.Start();
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
        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = listener.GetContext();
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

        // 1. Loopback RemoteEndPoint. Defense in depth on top of binding the literal 127.0.0.1
        // prefix: http.sys matches a request to a registered prefix by its Host header, not by
        // which interface the packet actually arrived on.
        var remote = request.RemoteEndPoint;
        if (remote is null || !IPAddress.IsLoopback(remote.Address))
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
        // Access-Control-* header.
        if (!string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
        {
            response.Headers["Allow"] = "POST";
            Reject(response, 405, "method not allowed");
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
    /// Constant-time comparison over UTF-8 bytes, per the task file: <see
    /// cref="CryptographicOperations.FixedTimeEquals"/> returns <see langword="false"/> immediately
    /// when the lengths differ (a documented, deliberate short-circuit -- a length mismatch is not a
    /// secret worth constant time over), and compares every byte otherwise.
    /// </summary>
    private bool HasValidToken(HttpListenerRequest request)
    {
        const string prefix = "Bearer ";
        var header = request.Headers["Authorization"];
        if (string.IsNullOrEmpty(header) || !header.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var providedBytes = Encoding.UTF8.GetBytes(header.AsSpan(prefix.Length).ToString());
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

    private void Reject(HttpListenerResponse response, int status, string reason)
    {
        _onDiagnostic($"alert-http rejected {status} {reason}");
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

        try
        {
            _listener?.Stop();
        }
        catch
        {
            // Best-effort: the goal below is only to make the blocked GetContext call return.
        }

        _thread?.Join(JoinTimeout);

        try
        {
            _listener?.Close();
        }
        catch
        {
            // Best-effort cleanup on the way out.
        }
    }
}
