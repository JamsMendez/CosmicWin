using System.Text;
using CosmicWin.Interop;

namespace CosmicWin.Interop.Tests;

/// <summary>
/// Pure wire-framing rules for the T4 alert pipe: the encoded-byte ceiling and the reply line
/// vocabulary, decided in <see cref="AlertPipeProtocol"/>'s own remarks.
/// </summary>
public sealed class AlertPipeProtocolTests
{
    [Fact]
    public void MaxMessageBytes_MatchesAlertCommandParsersCharacterLimit() =>
        Assert.Equal(256, AlertPipeProtocol.MaxMessageBytes);

    [Fact]
    public void TryEncode_AnAsciiCommandAtTheLimit_Succeeds()
    {
        var command = new string('a', AlertPipeProtocol.MaxMessageBytes);

        var ok = AlertPipeProtocol.TryEncode(command, out var bytes, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(AlertPipeProtocol.MaxMessageBytes, bytes.Length);
        Assert.Equal(command, Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void TryEncode_OnePastTheLimit_FailsWithTheOversizedReply()
    {
        var command = new string('a', AlertPipeProtocol.MaxMessageBytes + 1);

        var ok = AlertPipeProtocol.TryEncode(command, out var bytes, out var error);

        Assert.False(ok);
        Assert.Empty(bytes);
        Assert.Equal(AlertPipeProtocol.OversizedReply(AlertPipeProtocol.MaxMessageBytes + 1), error);
    }

    [Fact]
    public void TryEncode_MultiByteCharacters_AreCountedByEncodedBytesNotCharacters()
    {
        // Each 'warning' emoji-free multi-byte char (e.g. U+00F1 'ñ') costs 2 UTF-8 bytes, so 200
        // of them is 400 encoded bytes -- past the limit even though the string is 200 chars long.
        var command = new string('ñ', 200);

        var ok = AlertPipeProtocol.TryEncode(command, out var bytes, out var error);

        Assert.False(ok);
        Assert.Equal(400, Encoding.UTF8.GetByteCount(command));
        Assert.Equal(AlertPipeProtocol.OversizedReply(400), error);
    }

    [Fact]
    public void TryEncode_NullCommand_Throws() =>
        Assert.Throws<ArgumentNullException>(() => AlertPipeProtocol.TryEncode(null!, out _, out _));

    [Fact]
    public void OversizedReply_NamesTheActualByteCountAndTheLimit() =>
        Assert.Equal("error: command is 300 bytes long, past the 256-byte limit", AlertPipeProtocol.OversizedReply(300));

    [Fact]
    public void FormatError_PrependsTheErrorPrefix() =>
        Assert.Equal("error: busy", AlertPipeProtocol.FormatError("busy"));

    [Fact]
    public void OkReply_IsExactlyOk() => Assert.Equal("ok", AlertPipeProtocol.OkReply);

    [Fact]
    public void BusyReply_IsAWellFormedErrorLine() => Assert.Equal("error: busy", AlertPipeProtocol.BusyReply);

    [Fact]
    public void QueueFullReply_IsAWellFormedErrorLine() => Assert.Equal("error: queue full", AlertPipeProtocol.QueueFullReply);
}
