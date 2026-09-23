using System.IO.Pipes;
using CosmicWin.Interop;
using CosmicWin.Interop.Win32;
using CosmicWinAlert;

namespace CosmicWinAlert.Tests;

/// <summary>
/// The pure arg-joining and reply-to-exit-code mapping <see cref="Program"/> uses, tested
/// directly (T4 task file: "the client exe's arg handling/exit-code mapping as a unit-testable
/// function").
/// </summary>
public sealed class ProgramArgAndExitCodeTests
{
    [Fact]
    public void TryBuildCommand_NoArguments_FailsWithUsageError() =>
        Assert.False(Program.TryBuildCommand([], out _));

    [Fact]
    public void TryBuildCommand_OneArgument_IsUsedAsIs()
    {
        var ok = Program.TryBuildCommand(["warning:2 failed:1"], out var command);

        Assert.True(ok);
        Assert.Equal("warning:2 failed:1", command);
    }

    [Fact]
    public void TryBuildCommand_SeveralArguments_AreJoinedWithASingleSpace()
    {
        var ok = Program.TryBuildCommand(["warning:2", "failed:1", "duration:8"], out var command);

        Assert.True(ok);
        Assert.Equal("warning:2 failed:1 duration:8", command);
    }

    [Fact]
    public void Interpret_Ok_ReturnsExitOkAndPrintsNothing()
    {
        var stderr = new StringWriter();

        var exitCode = Program.Interpret(AlertPipeProtocol.OkReply, stderr);

        Assert.Equal(Program.ExitOk, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public void Interpret_AnErrorLine_ReturnsExitServerErrorAndPrintsItToStderr()
    {
        var stderr = new StringWriter();

        var exitCode = Program.Interpret("error: busy", stderr);

        Assert.Equal(Program.ExitServerError, exitCode);
        Assert.Equal("error: busy" + Environment.NewLine, stderr.ToString());
    }
}

/// <summary>
/// End-to-end coverage for <see cref="Program.RunAsync"/>: a real <see
/// cref="NamedPipeAlertCommandServer"/> on a unique test pipe name, the same "real pipe" approach
/// <c>NamedPipeAlertCommandServerTests</c> takes, cheap enough (task file: "one test that runs the
/// built client end to end is welcome if cheap") that all four exit-code paths get one each.
/// </summary>
public sealed class ProgramRunAsyncTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(2);

    private static string UniquePipeName([System.Runtime.CompilerServices.CallerMemberName] string testName = "") =>
        $"CosmicWin.Alerts.Tests.Client.{testName}.{Guid.NewGuid():N}";

    [Fact]
    public async Task RunAsync_NoArguments_ReturnsUsageErrorWithoutEverConnecting()
    {
        var stderr = new StringWriter();

        var exitCode = await Program.RunAsync(
            [], stderr, UniquePipeName(), ShortTimeout, ShortTimeout);

        Assert.Equal(Program.ExitUsageError, exitCode);
        Assert.NotEmpty(stderr.ToString());
    }

    [Fact]
    public async Task RunAsync_NoServerListening_ReturnsExitNoServerQuickly()
    {
        var stderr = new StringWriter();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        var exitCode = await Program.RunAsync(
            ["warning:1"], stderr, UniquePipeName(), TimeSpan.FromMilliseconds(300), ShortTimeout);

        elapsed.Stop();
        Assert.Equal(Program.ExitNoServer, exitCode);
        Assert.NotEmpty(stderr.ToString());
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), $"took {elapsed.Elapsed}");
    }

    [Fact]
    public async Task RunAsync_ServerAccepts_ReturnsExitOk()
    {
        var pipeName = UniquePipeName();
        using var server = new NamedPipeAlertCommandServer(pipeName, _ => AlertPipeProtocol.OkReply);
        server.Start();
        var stderr = new StringWriter();

        var exitCode = await Program.RunAsync(
            ["warning:2", "failed:1"], stderr, pipeName, ShortTimeout, ShortTimeout);

        Assert.Equal(Program.ExitOk, exitCode);
    }

    [Fact]
    public async Task RunAsync_ServerRejects_ReturnsExitServerErrorAndPrintsTheReplyToStderr()
    {
        var pipeName = UniquePipeName();
        using var server = new NamedPipeAlertCommandServer(
            pipeName, text => AlertPipeProtocol.FormatError($"'{text}' is not a 'key:count' token"));
        server.Start();
        var stderr = new StringWriter();

        var exitCode = await Program.RunAsync(["banana"], stderr, pipeName, ShortTimeout, ShortTimeout);

        Assert.Equal(Program.ExitServerError, exitCode);
        Assert.Equal("error: 'banana' is not a 'key:count' token" + Environment.NewLine, stderr.ToString());
    }

    /// <summary>
    /// Finding R3-client-unmapped-failures: a server that accepts the connection and reads the
    /// command, but then closes without ever writing a reply, used to make <c>ReadAsync</c> return
    /// zero bytes -- an empty string is not <see cref="AlertPipeProtocol.OkReply"/>, so the OLD code
    /// fell through to <see cref="Program.Interpret"/> and returned <see
    /// cref="Program.ExitServerError"/> (1), an undocumented mapping for "the server never actually
    /// answered." This must be <see cref="Program.ExitNoServer"/> (2), the same code every other "no
    /// usable reply" shape uses.
    /// </summary>
    [Fact]
    public async Task RunAsync_ServerClosesWithoutReplying_ReturnsExitNoServer()
    {
        var pipeName = UniquePipeName();
        var serverTask = Task.Run(async () =>
        {
            using var server = new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Message, PipeOptions.Asynchronous);
            await server.WaitForConnectionAsync();
            var buffer = new byte[64];
            await server.ReadAsync(buffer); // reads the command, then the using block closes
        });                                 // without ever writing back -- no reply, ever.

        var stderr = new StringWriter();

        var exitCode = await Program.RunAsync(["warning:1"], stderr, pipeName, ShortTimeout, ShortTimeout);

        Assert.Equal(Program.ExitNoServer, exitCode);
        Assert.NotEmpty(stderr.ToString());
        await serverTask;
    }

    /// <summary>
    /// Finding R3-client-unmapped-failures: the server accepts the connection, then severs it (an
    /// <see cref="IOException"/>-flavoured broken pipe, e.g. <c>ERROR_BROKEN_PIPE</c> /
    /// <c>ERROR_PIPE_NOT_CONNECTED</c>) before the client's write-then-read round trip can finish.
    /// The OLD code caught only <see cref="OperationCanceledException"/> around this call, so an
    /// <see cref="IOException"/> here used to propagate all the way out of <c>Main</c> as an
    /// unhandled exception instead of a documented exit code.
    /// </summary>
    [Fact]
    public async Task RunAsync_ServerDisconnectsMidCall_ReturnsExitNoServerWithoutThrowing()
    {
        var pipeName = UniquePipeName();
        var serverTask = Task.Run(async () =>
        {
            using var server = new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Message, PipeOptions.Asynchronous);
            await server.WaitForConnectionAsync();
            server.Disconnect(); // severs the connection before ever reading or replying.
        });

        var stderr = new StringWriter();

        var exitCode = await Program.RunAsync(["warning:1"], stderr, pipeName, ShortTimeout, ShortTimeout);

        Assert.Equal(Program.ExitNoServer, exitCode);
        Assert.NotEmpty(stderr.ToString());
        await serverTask;
    }
}
