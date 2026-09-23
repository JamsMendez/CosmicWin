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

    /// <summary>
    /// Finding R3-oversized-reply-size: a message too large to fit the whole read buffer only ever
    /// has a TRUNCATED read count available on the server -- reporting that truncated count as the
    /// message's size (the old behaviour) is misleading, since the sender's real message was larger.
    /// The reply must instead be an honest lower bound.
    /// </summary>
    [Fact]
    public async Task SeverelyOversizedMessage_ThatDoesNotFitTheReadBuffer_ReportsAnHonestLowerBoundNotTheTruncatedCount()
    {
        var pipeName = UniquePipeName();
        using var server = new NamedPipeAlertCommandServer(pipeName, _ => AlertPipeProtocol.OkReply);
        server.Start();

        // Larger than NamedPipeAlertCommandServer.ReadBufferSize: the server's one ReadAsync call
        // can only ever see the first ReadBufferSize bytes of this, and IsMessageComplete reports
        // false for the rest.
        var severelyOversized = new string('w', NamedPipeAlertCommandServer.ReadBufferSize + 500);
        var reply = await SendAsync(pipeName, severelyOversized);

        Assert.Equal(AlertPipeProtocol.OversizedReplyAtLeast(NamedPipeAlertCommandServer.ReadBufferSize), reply);
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

    /// <summary>
    /// Finding R3-runloop-hot-spin: <see cref="NamedPipeAlertCommandServer"/> creates its pipe
    /// instance with <c>nMaxInstances = 1</c>, so a second server started on the SAME pipe name can
    /// never create its own instance -- every one of its <c>RunLoop</c> iterations fails the same
    /// way, forever. Before the fix this retried with no delay at all (a genuine hot spin: one CPU
    /// core pegged, and the diagnostic callback flooded as fast as the loop could run). With the
    /// 100&#8201;ms&#8594;5&#8201;s exponential backoff, the number of failures (and therefore
    /// diagnostics) over any bounded window is small and predictable, and the healthy first server
    /// is completely unaffected.
    /// </summary>
    [Fact]
    public async Task TwoServersOnTheSamePipeName_TheSecondBacksOffAndTheFirstKeepsServing()
    {
        var pipeName = UniquePipeName();
        using var first = new NamedPipeAlertCommandServer(pipeName, _ => AlertPipeProtocol.OkReply);
        first.Start();

        // Give the first server's background thread time to actually own the pipe instance before
        // the second one starts racing it -- otherwise which one wins the collision is a coin flip.
        Thread.Sleep(200);

        var collisionDiagnostics = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var second = new NamedPipeAlertCommandServer(pipeName, _ => AlertPipeProtocol.OkReply, collisionDiagnostics.Enqueue);
        second.Start();

        // A short, bounded window for the collision to keep retrying in the background.
        await Task.Delay(TimeSpan.FromSeconds(3));

        // The first, healthy server must be completely unaffected by the second one's failures.
        var reply = await SendAsync(pipeName, "warning:1");
        Assert.Equal("ok", reply);

        // A hot spin (no backoff at all) would log many thousands of times in 3 seconds; with
        // 100ms doubling up to 5s, the cumulative wait after 5 failures already exceeds 3s (100 +
        // 200 + 400 + 800 + 1600 = 3100ms), so the count is small -- a generous upper bound that
        // would only be reached by something far closer to a hot spin than real backoff.
        Assert.InRange(collisionDiagnostics.Count, 1, 25);
    }

    /// <summary>
    /// Finding R3-reply-drain-unbounded: the old <c>WriteReplyAsync</c> called <see
    /// cref="PipeStream.WaitForPipeDrain"/> with no timeout, which blocks until the client has read
    /// everything or the connection breaks. A same-user client that connects, sends a valid
    /// command, and then simply never reads its reply used to hang that call -- and, because this
    /// server's pipe is <c>nMaxInstances = 1</c>, hang the single instance -- forever. The reply
    /// path must now give up after a bound (<see cref="NamedPipeAlertCommandServer.ReplyTimeout"/>)
    /// and move on to the next connection.
    /// </summary>
    /// <remarks>
    /// Finding R3-drain-test-does-not-prove-timeout (review of T4b): the ORIGINAL version of this
    /// test disposed the rude client, ending its connection, BEFORE the second client connected.
    /// Disposing a <see cref="NamedPipeClientStream"/> breaks the pipe by itself -- which would
    /// unblock even the OLD, unbounded <see cref="PipeStream.WaitForPipeDrain"/> just as well as the
    /// new bounded one, so that version proved nothing about <see
    /// cref="NamedPipeAlertCommandServer.ReplyTimeout"/> specifically. This version keeps the rude
    /// client open and non-reading for the ENTIRE test, including while the second client connects
    /// and is served -- confirmed by re-disabling the bound and observing this exact test fail (see
    /// the T4c task file entry for the RED/GREEN lines).
    /// </remarks>
    [Fact]
    public async Task AClientThatWritesAndNeverReads_DoesNotStallTheServerFromServingTheNextClient()
    {
        var pipeName = UniquePipeName();
        using var server = new NamedPipeAlertCommandServer(pipeName, _ => AlertPipeProtocol.OkReply);
        server.Start();

        // Stays connected for the rest of the test -- NOT disposed before the second client
        // connects -- so only the server's own ReplyTimeout, never the rude client disconnecting,
        // can be what frees the single pipe instance below.
        using var rudeClient = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using (var connectTimeout = new CancellationTokenSource(ShortTimeout))
        {
            await rudeClient.ConnectAsync(connectTimeout.Token);
        }
        rudeClient.ReadMode = PipeTransmissionMode.Message;

        using (var writeTimeout = new CancellationTokenSource(ShortTimeout))
        {
            // Writes a genuinely valid command and then, deliberately, never reads the reply --
            // and, critically, never disconnects either.
            await rudeClient.WriteAsync(Encoding.UTF8.GetBytes("warning:1"), writeTimeout.Token);
            await rudeClient.FlushAsync(writeTimeout.Token);
        }

        // A well-behaved second client, arriving WHILE the rude one is still connected and still
        // not reading, must still be served -- within a generous bound above ReplyTimeout, not
        // depending on exact timing.
        var bound = NamedPipeAlertCommandServer.ReplyTimeout + TimeSpan.FromSeconds(3);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var reply = await SendAsync(pipeName, "warning:1");
        elapsed.Stop();

        Assert.Equal("ok", reply);
        Assert.True(elapsed.Elapsed < bound, $"The next client took {elapsed.Elapsed} to be served (bound {bound}).");
    }

    /// <summary>Finding R3-reply-drain-unbounded: Dispose must stay prompt even while a connection is stuck mid-reply-drain.</summary>
    [Fact]
    public async Task Dispose_WhileAClientIsStuckNotReadingTheReply_ReturnsPromptly()
    {
        var pipeName = UniquePipeName();
        var server = new NamedPipeAlertCommandServer(pipeName, _ => AlertPipeProtocol.OkReply);
        server.Start();

        using var rudeClient = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var connectTimeout = new CancellationTokenSource(ShortTimeout);
        await rudeClient.ConnectAsync(connectTimeout.Token);
        rudeClient.ReadMode = PipeTransmissionMode.Message;

        using var writeTimeout = new CancellationTokenSource(ShortTimeout);
        await rudeClient.WriteAsync(Encoding.UTF8.GetBytes("warning:1"), writeTimeout.Token);
        await rudeClient.FlushAsync(writeTimeout.Token);

        // Give the server a moment to actually reach the write/drain step before disposing.
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        server.Dispose();
        elapsed.Stop();

        Assert.True(elapsed.Elapsed < ShortTimeout, $"Dispose took {elapsed.Elapsed} with a stuck reply drain.");
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
