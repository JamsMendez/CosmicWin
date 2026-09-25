using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CosmicWin.Interop;

/// <summary>
/// The pure rules of the localhost HTTP alert endpoint (feature http-alert-endpoint): the route,
/// the body ceiling, how a JSON body becomes the named pipe's <c>kind:count</c> command text, and
/// how the shared command handler's reply becomes an HTTP status code.
/// </summary>
/// <remarks>
/// <para>
/// The body is <c>{ "warning": 2, "failed": 1, "duration": 5 }</c>, every field optional. It is
/// translated field by field, in the order written, into <c>"warning:2 failed:1 duration:5"</c> and
/// handed to the SAME handler the pipe uses. So this class checks only the JSON shape: the counts,
/// the duration range, repeated fields and "at least one group" stay owned by
/// <c>AlertCommandParser</c>, and both transports reject exactly the same commands.
/// </para>
/// <para>
/// Replies reuse <see cref="AlertPipeProtocol"/>'s vocabulary (<c>"ok"</c> / <c>"error: ..."</c>)
/// as the response body; only the status code is HTTP-specific.
/// </para>
/// </remarks>
public static class AlertHttpProtocol
{
    /// <summary>The only route the endpoint answers.</summary>
    public const string AlertsPath = "/v1/alerts";

    /// <summary>
    /// Request bodies past this many bytes are rejected unread. The largest meaningful body is well
    /// under 100 bytes; this leaves room for whitespace without letting a caller stream megabytes.
    /// </summary>
    public const int MaxBodyBytes = 1024;

    /// <summary>The port used when the settings file does not name one.</summary>
    public const int DefaultPort = 47811;

    private const string InvalidJson = "body is not valid JSON";

    private static readonly HashSet<string> KnownFields = new(StringComparer.Ordinal)
    {
        "warning", "failed", "duration",
    };

    /// <summary>
    /// Translates <paramref name="body"/> into command text. Never throws: a malformed body is an
    /// everyday event from outside the process.
    /// </summary>
    public static bool TryTranslate(string? body, out string? command, out string? error)
    {
        command = null;

        if (body is null)
        {
            error = InvalidJson;
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "body must be a JSON object";
                return false;
            }

            var tokens = new List<string>();
            foreach (var field in document.RootElement.EnumerateObject())
            {
                if (!KnownFields.Contains(field.Name))
                {
                    error = $"unknown field '{field.Name}'";
                    return false;
                }

                if (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetInt32(out var number))
                {
                    error = $"field '{field.Name}' must be a whole number";
                    return false;
                }

                tokens.Add(string.Create(CultureInfo.InvariantCulture, $"{field.Name}:{number}"));
            }

            command = string.Join(' ', tokens);
            error = null;
            return true;
        }
        catch (JsonException)
        {
            error = InvalidJson;
            return false;
        }
    }

    /// <summary>
    /// The status code for a reply from the shared alert command handler: 202 accepted, 429 queue
    /// full, 503 alerts disabled, 400 any other rejected command, 500 anything unrecognised.
    /// </summary>
    public static int StatusCodeFor(string reply) => reply switch
    {
        AlertPipeProtocol.OkReply => 202,
        AlertPipeProtocol.QueueFullReply => 429,
        _ when reply == AlertPipeProtocol.FormatError("alerts are disabled") => 503,
        _ when reply == AlertPipeProtocol.FormatError("internal error") => 500,
        _ when reply.StartsWith(AlertPipeProtocol.FormatError(string.Empty), StringComparison.Ordinal) => 400,
        _ => 500,
    };
}
