using System.Text;

namespace CosmicWin.Interop;

/// <summary>
/// The wire framing for the alert named pipe (plan &#167;4/&#167;6, T4): one UTF-8 text message per
/// connection, capped at <see cref="MaxMessageBytes"/> encoded bytes, and one short UTF-8 reply
/// message before the server closes the connection -- <see cref="OkReply"/>, or an
/// <c>"error: &lt;reason&gt;"</c> line built with <see cref="FormatError"/>.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately pure and Win32-free, so it lives beside <see cref="AlertPipeName"/> in the root
/// <c>CosmicWin.Interop</c> namespace rather than under <c>Win32</c>: the client
/// (<c>CosmicWinAlert.exe</c>) references this project only for these two pure pieces, never for
/// anything that touches a native handle, which is what keeps it free of WPF and the elevation
/// manifest that make <c>CosmicWin.App</c> the wrong place to share this from.
/// </para>
/// <para>
/// <see cref="MaxMessageBytes"/> matches <c>AlertCommandParser</c>'s 256-character input ceiling
/// (App/Alerts/AlertCommandParser.cs, T1). It is expressed in ENCODED bytes here, one layer below
/// the parser's character count, because the pipe never sees a <see cref="string"/> -- only the
/// bytes a message-mode <c>WriteFile</c> call delivered -- and a command built only from the ASCII
/// token grammar the parser accepts (letters, digits, ':', ' ') encodes to exactly as many bytes as
/// characters, so the two ceilings agree for every input the parser could ever accept.
/// </para>
/// </remarks>
public static class AlertPipeProtocol
{
    /// <summary>
    /// The encoded-byte ceiling for one command message, matching
    /// <c>AlertCommandParser</c>'s 256-character limit (T1).
    /// </summary>
    public const int MaxMessageBytes = 256;

    /// <summary>The reply line the server sends when the handler accepted the command.</summary>
    public const string OkReply = "ok";

    /// <summary>The reply line for a command rejected only because another client already has the server busy.</summary>
    public const string BusyReply = "error: busy";

    /// <summary>The reply line for a command rejected only because the alert queue is already full.</summary>
    public const string QueueFullReply = "error: queue full";

    private const string ErrorPrefix = "error: ";

    /// <summary>Builds an <c>"error: &lt;reason&gt;"</c> reply line from a short, human-readable reason.</summary>
    public static string FormatError(string reason) => ErrorPrefix + reason;

    /// <summary>
    /// The reply for a command message that arrived past <see cref="MaxMessageBytes"/> -- rejected
    /// outright, the same "never partially apply" contract <c>AlertCommandParser</c> already keeps
    /// for its own, narrower length check.
    /// </summary>
    public static string OversizedReply(int actualBytes) =>
        FormatError($"command is {actualBytes} bytes long, past the {MaxMessageBytes}-byte limit");

    /// <summary>
    /// Encodes <paramref name="command"/> as UTF-8 for the wire, failing when it would exceed
    /// <see cref="MaxMessageBytes"/> -- the same check the server applies to what it reads, run
    /// once up front so a client never sends a message the server is guaranteed to reject.
    /// </summary>
    public static bool TryEncode(string command, out byte[] bytes, out string? error)
    {
        ArgumentNullException.ThrowIfNull(command);

        bytes = Encoding.UTF8.GetBytes(command);
        if (bytes.Length > MaxMessageBytes)
        {
            error = OversizedReply(bytes.Length);
            bytes = [];
            return false;
        }

        error = null;
        return true;
    }
}
