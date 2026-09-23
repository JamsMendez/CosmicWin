using System.IO.Pipes;
using System.Text;
using CosmicWin.Interop;

namespace CosmicWinAlert;

/// <summary>
/// Unelevated <c>asInvoker</c> client for the T4 alert named pipe: joins its arguments into one
/// command string, sends it to the elevated <c>CosmicWin.App.exe</c>, and reports the reply as a
/// process exit code.
/// </summary>
/// <remarks>
/// <para>
/// Decided 2026-09-23 (plan &#167;4/&#167;6, task file T4 entry): <c>CosmicWin.App.exe</c> runs
/// <c>requireAdministrator</c>, so an <c>--alert</c> flag on it would raise UAC on every alert sent
/// from a normal shell or WSL. This is the separate, always-<c>asInvoker</c> process that decision
/// calls for -- it sends one message and exits, exactly like <c>CosmicWin.Launcher.Program</c>'s
/// own thin, directly-testable shape (<c>Main</c> delegates everything to a method a test can call
/// with substituted timeouts and captured output, never spawning the real process).
/// </para>
/// <para>
/// References ONLY <c>CosmicWin.Interop</c>, and only for its two Win32-free members --
/// <see cref="AlertPipeName"/> and <see cref="AlertPipeProtocol"/> -- never <c>CosmicWin.App</c>,
/// which carries WPF and the elevation manifest this project must not have.
/// <see cref="System.IO.Pipes.NamedPipeClientStream"/> needs no custom ACL to connect (only the
/// SERVER'S handle carries the security descriptor T4 built), so this project touches no Win32 of
/// its own at all.
/// </para>
/// </remarks>
public static class Program
{
    /// <summary>The server accepted the command.</summary>
    public const int ExitOk = 0;

    /// <summary>The server replied with an error line (printed to stderr): a malformed command, "busy", or "queue full".</summary>
    public const int ExitServerError = 1;

    /// <summary>No CosmicWin instance is listening -- connect timed out, or the server did not reply in time.</summary>
    public const int ExitNoServer = 2;

    /// <summary>No command was given on the command line.</summary>
    public const int ExitUsageError = 3;

    /// <summary>Plan &#167;6, T4: "connect timeout ~1 s".</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Bounds the write-reply-then-read round trip once connected, so a stuck server cannot hang this client forever either.</summary>
    public static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(2);

    public static int Main(string[] args) =>
        RunAsync(args, Console.Error, AlertPipeName.Resolve(), ConnectTimeout, IoTimeout)
            .GetAwaiter().GetResult();

    /// <summary>
    /// The whole client, minus process exit. <paramref name="pipeName"/>, <paramref
    /// name="connectTimeout"/> and <paramref name="ioTimeout"/> are parameters rather than
    /// <see cref="AlertPipeName.Resolve()"/>/<see cref="ConnectTimeout"/>/<see cref="IoTimeout"/>
    /// read directly, so a test can point this at a unique test pipe with a short timeout instead
    /// of the live per-user one -- the same reasoning <c>CosmicWin.Launcher.Program.Run</c> takes
    /// an <c>IProcessRunner</c> rather than calling <c>Process.Start</c> itself.
    /// </summary>
    public static async Task<int> RunAsync(
        string[] args, TextWriter stderr, string pipeName, TimeSpan connectTimeout, TimeSpan ioTimeout)
    {
        if (!TryBuildCommand(args, out var command))
        {
            stderr.WriteLine("usage: CosmicWinAlert.exe <kind:count> [kind:count ...] [duration:seconds]");
            return ExitUsageError;
        }

        if (!AlertPipeProtocol.TryEncode(command, out var bytes, out var encodeError))
        {
            // Caught here rather than left for the server: the same 256-byte ceiling the server
            // would reject it with anyway, so failing before ever connecting saves a round trip.
            stderr.WriteLine(encodeError);
            return ExitServerError;
        }

        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            using var connecting = new CancellationTokenSource(connectTimeout);
            await client.ConnectAsync(connecting.Token).ConfigureAwait(false);

            // Matches the server's own PIPE_READMODE_MESSAGE (NamedPipeAlertCommandServer): each
            // side of a message-type pipe sets its OWN read mode independently, so the client has
            // to ask for message framing too, or its read below would just see an undifferentiated
            // byte stream instead of exactly the server's one reply line.
            client.ReadMode = PipeTransmissionMode.Message;

            using var callBudget = new CancellationTokenSource(ioTimeout);
            await client.WriteAsync(bytes, callBudget.Token).ConfigureAwait(false);
            await client.FlushAsync(callBudget.Token).ConfigureAwait(false);

            var buffer = new byte[1024];
            var read = await client.ReadAsync(buffer, callBudget.Token).ConfigureAwait(false);
            if (read == 0)
            {
                // The connection reached EOF without ever carrying a reply byte -- the server
                // closed (or was killed) after accepting but before answering. Finding
                // R3-client-unmapped-failures: this is a "no usable reply" shape, exactly like a
                // connect timeout, not a malformed-command server error -- an empty string is not
                // AlertPipeProtocol.OkReply, so falling through to Interpret used to return
                // ExitServerError (1), an undocumented mapping for this case.
                stderr.WriteLine("CosmicWin closed the connection without replying.");
                return ExitNoServer;
            }

            var reply = Encoding.UTF8.GetString(buffer, 0, read);
            return Interpret(reply, stderr);
        }
        catch (OperationCanceledException)
        {
            // Either the connect budget or the call budget expired: no server listening, or one
            // that accepted but never finished the round trip in time.
            stderr.WriteLine("CosmicWin did not reply in time.");
            return ExitNoServer;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Finding R3-client-unmapped-failures: every OTHER documented client failure shape --
            // connect failure (e.g. "all pipe instances are busy"), access denied (wrong ACL/
            // integrity level), or a broken pipe mid-call (the server crashed or disconnected
            // after accepting) -- surfaces as one of these two exception types. Previously only
            // OperationCanceledException was caught here, so any of these propagated out of
            // RunAsync as an unhandled exception instead of a documented exit code.
            stderr.WriteLine($"CosmicWin is not running, or is not listening for alerts: {error.Message}");
            return ExitNoServer;
        }
    }

    /// <summary>
    /// Joins every argument with a single space, so <c>CosmicWinAlert.exe warning:2 failed:1</c>
    /// (two shell-split args) and <c>CosmicWinAlert.exe "warning:2 failed:1"</c> (one quoted arg)
    /// both reach <c>AlertCommandParser</c> as the identical string. Fails only when there are no
    /// arguments at all -- an empty JOIN of a non-empty array can still be a real (if useless)
    /// command string, so "no arguments" is the one shape worth telling apart as usage error.
    /// </summary>
    internal static bool TryBuildCommand(string[] args, out string command)
    {
        if (args.Length == 0)
        {
            command = string.Empty;
            return false;
        }

        command = string.Join(' ', args);
        return true;
    }

    /// <summary>Maps the server's one reply line to an exit code, printing it to <paramref name="stderr"/> whenever it is not <see cref="AlertPipeProtocol.OkReply"/>.</summary>
    internal static int Interpret(string reply, TextWriter stderr)
    {
        if (reply == AlertPipeProtocol.OkReply)
        {
            return ExitOk;
        }

        stderr.WriteLine(reply);
        return ExitServerError;
    }
}
