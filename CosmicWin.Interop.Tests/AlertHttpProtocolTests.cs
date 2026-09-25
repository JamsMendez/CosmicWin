using CosmicWin.Interop;

namespace CosmicWin.Interop.Tests;

/// <summary>
/// Pure rules for the HTTP alert endpoint (feature http-alert-endpoint, H1): how a JSON body becomes
/// the pipe's <c>kind:count</c> command text, and how the shared handler's reply becomes a status
/// code. Counts and duration limits are NOT checked here -- <c>AlertCommandParser</c> owns them for
/// both transports.
/// </summary>
public sealed class AlertHttpProtocolTests
{
    [Theory]
    [InlineData("""{"warning":2}""", "warning:2")]
    [InlineData("""{"failed":1}""", "failed:1")]
    [InlineData("""{"warning":2,"failed":1,"duration":5}""", "warning:2 failed:1 duration:5")]
    [InlineData("""{"duration":5,"failed":1,"warning":2}""", "duration:5 failed:1 warning:2")]
    [InlineData("""  { "warning" : 3 }  """, "warning:3")]
    [InlineData("""{}""", "")]
    public void TryTranslate_AValidBody_KeepsTheFieldsInTheOrderWritten(string body, string expected)
    {
        var ok = AlertHttpProtocol.TryTranslate(body, out var command, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(expected, command);
    }

    [Fact]
    public void TryTranslate_ARepeatedField_IsPassedThroughForTheParserToReject() =>
        Assert.Equal("warning:1 warning:2", Translate("""{"warning":1,"warning":2}"""));

    [Fact]
    public void TryTranslate_ANegativeCount_IsPassedThroughForTheParserToReject() =>
        Assert.Equal("warning:-1", Translate("""{"warning":-1}"""));

    [Theory]
    [InlineData("", "body is not valid JSON")]
    [InlineData("{", "body is not valid JSON")]
    [InlineData("warning:2", "body is not valid JSON")]
    [InlineData("[1]", "body must be a JSON object")]
    [InlineData("2", "body must be a JSON object")]
    [InlineData("null", "body must be a JSON object")]
    [InlineData("""{"Warning":1}""", "unknown field 'Warning'")]
    [InlineData("""{"info":1}""", "unknown field 'info'")]
    [InlineData("""{"warning":"2"}""", "field 'warning' must be a whole number")]
    [InlineData("""{"warning":1.5}""", "field 'warning' must be a whole number")]
    [InlineData("""{"warning":true}""", "field 'warning' must be a whole number")]
    [InlineData("""{"duration":null}""", "field 'duration' must be a whole number")]
    [InlineData("""{"failed":99999999999}""", "field 'failed' must be a whole number")]
    public void TryTranslate_AnInvalidBody_FailsWithAShortReason(string body, string expected)
    {
        var ok = AlertHttpProtocol.TryTranslate(body, out var command, out var error);

        Assert.False(ok);
        Assert.Null(command);
        Assert.Equal(expected, error);
    }

    [Fact]
    public void TryTranslate_ANullBody_Fails()
    {
        var ok = AlertHttpProtocol.TryTranslate(null, out var command, out var error);

        Assert.False(ok);
        Assert.Null(command);
        Assert.Equal("body is not valid JSON", error);
    }

    [Theory]
    [InlineData(AlertPipeProtocol.OkReply, 202)]
    [InlineData(AlertPipeProtocol.QueueFullReply, 429)]
    [InlineData("error: alerts are disabled", 503)]
    [InlineData("error: 'warning:0' must be 1..16", 400)]
    [InlineData("error: internal error", 500)]
    [InlineData("something unexpected", 500)]
    public void StatusCodeFor_MapsTheHandlerReply(string reply, int expected) =>
        Assert.Equal(expected, AlertHttpProtocol.StatusCodeFor(reply));

    [Fact]
    public void Constants_MatchThePlan()
    {
        Assert.Equal("/v1/alerts", AlertHttpProtocol.AlertsPath);
        Assert.Equal(1024, AlertHttpProtocol.MaxBodyBytes);
        Assert.Equal(47811, AlertHttpProtocol.DefaultPort);
    }

    private static string? Translate(string body)
    {
        Assert.True(AlertHttpProtocol.TryTranslate(body, out var command, out var error), error);
        return command;
    }
}
