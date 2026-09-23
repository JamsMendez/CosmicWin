using System.IO.Pipes;
using System.Text;
using CosmicWin.Interop;
using CosmicWin.Interop.Win32;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// Integration coverage for <see cref="NamedPipeAlertCommandServer"/>: a real named pipe, a real
/// <see cref="NamedPipeClientStream"/>, no fakes -- the task file (T4) asks for "integration: real
/// pipe, in-process server with a unique test pipe name". Every fact gets its own pipe name (a
/// GUID suffix) even though <c>TestParallelism.cs</c> already serialises this assembly, so a
/// leftover pipe instance from a previous run can never collide with this one.
/// </summary>
public sealed class NamedPipeAlertCommandServerTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);

    private static string UniquePipeName([System.Runtime.CompilerServices.CallerMemberName] string testName = "") =>
        $"CosmicWin.Alerts.Tests.{testName}.{Guid.NewGuid():N}";

    [Fact]
    public async Task RoundTrip_HandlerAccepts_ClientReceivesOk()
    {
        var pipeName = UniquePipeName();
        using var server = new NamedPipeAlertCommandServer(pipeName, _ => AlertPipeProtocol.OkReply);
        server.Start();

        var reply = await SendAsync(pipeName, "warning:2");

        Assert.Equal("ok", reply);
    }

    [Fact]
    public async Task RoundTrip_HandlerRejects_ClientReceivesTheHandlersErrorLineVerbatim()
    {
        // The parser lives in CosmicWin.App (T1); this layer only proves the server relays
        // whatever the handler decides, unchanged -- "malformed command -> error reply" (T4 task
        // list) is the handler's call, made here by a fake handler standing in for T8's real one.
        var pipeName = UniquePipeName();
        using var server = new NamedPipeAlertCommandServer(
            pipeName, text => AlertPipeProtocol.FormatError($"'{text}' is not a 'key:count' token"));
        server.Start();

        var reply = await SendAsync(pipeName, "banana");

        Assert.Equal("error: 'banana' is not a 'key:count' token", reply);
    }

    [Fact]
    public async Task OversizedMessage_IsRejectedWithoutEverCallingTheHandler()
    {
        var pipeName = UniquePipeName();
        var handlerCalls = 0;
        using var server = new NamedPipeAlertCommandServer(pipeName, text =>
        {
            Interlocked.Increment(ref handlerCalls);
            return AlertPipeProtocol.OkReply;
        });
        server.Start();

        var oversized = new string('w', AlertPipeProtocol.MaxMessageBytes + 50);
        var reply = await SendAsync(pipeName, oversized);

        Assert.Equal(AlertPipeProtocol.OversizedReply(oversized.Length), reply);
        Assert.Equal(0, handlerCalls);
    }

    [Fact]
    public async Task ConnectingWithNoServerListening_FailsQuickly()
    {
        var pipeName = UniquePipeName();
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.ConnectAsync(connectTimeout.Token));
        elapsed.Stop();

        Assert.True(elapsed.Elapsed < ShortTimeout, $"Connecting to a nonexistent pipe took {elapsed.Elapsed}.");
    }

    [Fact]
    public async Task ASlowSilentClient_TimesOutWithoutStoppingTheServerFromServingTheNextClient()
    {
        var pipeName = UniquePipeName();
        using var server = new NamedPipeAlertCommandServer(pipeName, _ => AlertPipeProtocol.OkReply);
        server.Start();

        // Connects, then deliberately never writes -- the server's own ConnectionReadTimeout (2s)
        // must give up on it, not the test's timeout below.
        using (var slowClient = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            using var connectTimeout = new CancellationTokenSource(ShortTimeout);
            await slowClient.ConnectAsync(connectTimeout.Token);

            // Long enough to be certain the server's own 2s read timeout has already fired and
            // moved on, without depending on exact timing.
            await Task.Delay(NamedPipeAlertCommandServer.ConnectionReadTimeout + TimeSpan.FromSeconds(1));
        }

        // A well-behaved client arriving afterwards must still be served normally.
        var reply = await SendAsync(pipeName, "warning:1");
        Assert.Equal("ok", reply);
    }

    [Fact]
    public void Dispose_WhileIdle_ReturnsPromptly()
    {
        var pipeName = UniquePipeName();
        var server = new NamedPipeAlertCommandServer(pipeName, _ => AlertPipeProtocol.OkReply);
        server.Start();

        // Give the background thread a moment to actually reach WaitForConnectionAsync, so Dispose
        // is proven to cancel a genuinely pending wait rather than racing the thread's own startup.
        Thread.Sleep(200);

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        server.Dispose();
        elapsed.Stop();

        Assert.True(elapsed.Elapsed < ShortTimeout, $"Dispose took {elapsed.Elapsed} while idle.");
    }

    [Fact]
    public void Constructor_NullHandler_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new NamedPipeAlertCommandServer(UniquePipeName(), null!));

    [Fact]
    public void Constructor_BlankPipeName_Throws() =>
        Assert.Throws<ArgumentException>(() => new NamedPipeAlertCommandServer(" ", _ => "ok"));

    /// <summary>Connects, sends one message, reads the one reply line, disposes -- the client half of the T4 protocol.</summary>
    private static async Task<string> SendAsync(string pipeName, string command)
    {
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var connectTimeout = new CancellationTokenSource(ShortTimeout);
        await client.ConnectAsync(connectTimeout.Token);
        client.ReadMode = PipeTransmissionMode.Message;

        using var callTimeout = new CancellationTokenSource(ShortTimeout);
        await client.WriteAsync(Encoding.UTF8.GetBytes(command), callTimeout.Token);
        await client.FlushAsync(callTimeout.Token);

        var buffer = new byte[1024];
        var read = await client.ReadAsync(buffer, callTimeout.Token);
        return Encoding.UTF8.GetString(buffer, 0, read);
    }
}
