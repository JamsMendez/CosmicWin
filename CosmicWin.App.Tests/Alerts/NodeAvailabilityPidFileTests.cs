namespace CosmicWin.App.Tests.Alerts;

/// <summary>
/// R3-tryreadpid-terminator-coverage-node-gated: <see cref="NodeAvailability.TryReadPid"/>'s
/// terminator rule was only exercised by the Node-gated process tests, so a machine without Node
/// skipped every one of them and nothing guarded the rule. These are plain facts over files written
/// here directly -- no process, no Node.
/// </summary>
public sealed class NodeAvailabilityPidFileTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "cosmicwin-pidfile-" + Guid.NewGuid().ToString("N"));

    public NodeAvailabilityPidFileTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }

    [Theory]
    [InlineData("12345\n", 12345)]
    [InlineData("12345\r\n", 12345)]
    public void TryReadPid_APidWithItsTerminator_IsRead(string content, int expected)
    {
        var path = Write(content);

        Assert.True(NodeAvailability.TryReadPid(path, out var pid));
        Assert.Equal(expected, pid);
    }

    /// <summary>
    /// "123" is the case the rule exists for: a reader landing mid-write sees a VALID int that is the
    /// wrong pid. Empty is a reader landing between create and write; the rest are not pids at all --
    /// including zero and negatives (R3-pidfile-test-nonpositive-pid-unpinned): no real process has
    /// one, and Process.GetProcessById(0) is the System Idle Process, never a test script.
    /// </summary>
    [Theory]
    [InlineData("123")]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("abc\n")]
    [InlineData("0\n")]
    [InlineData("-5\n")]
    public void TryReadPid_AnythingButATerminatedPid_IsNotRead(string content)
    {
        var path = Write(content);

        Assert.False(NodeAvailability.TryReadPid(path, out var pid));
        Assert.Equal(0, pid);
    }

    [Fact]
    public void TryReadPid_AMissingFile_IsNotRead()
    {
        Assert.False(NodeAvailability.TryReadPid(Path.Combine(_directory, "missing.pid"), out var pid));
        Assert.Equal(0, pid);
    }

    private string Write(string content)
    {
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".pid");
        File.WriteAllText(path, content);
        return path;
    }
}
