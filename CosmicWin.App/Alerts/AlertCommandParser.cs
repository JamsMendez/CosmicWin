using System.Globalization;

namespace CosmicWin.App.Alerts;

/// <summary>
/// Parses the argument string after <c>--alert</c> (for example <c>"warning:2 failed:1"</c>)
/// into an <see cref="AlertCommand"/>.
/// </summary>
/// <remarks>
/// <para>
/// Grammar, decided 2026-09-23 (plan &#167;4/&#167;6, T1): whitespace-separated <c>kind:count</c>
/// tokens, <c>kind</c> one of <c>warning</c> / <c>failed</c> (case-insensitive), <c>count</c> a
/// plain decimal integer 1..16, each key written at most once, plus an optional
/// <c>duration:seconds</c> token (1..60, default 5). At least one <c>warning</c>/<c>failed</c>
/// group is required, and the tile counts must not sum past 16 -- the same ceiling
/// <see cref="AlertTileLayout"/> is designed against.
/// </para>
/// <para>
/// Deliberately never throws. The input reaches this parser from outside the process, over the
/// named pipe T4 adds, so a malformed command is an everyday event rather than a bug -- it is
/// rejected and logged, never allowed to take the wallpaper process down with it.
/// </para>
/// </remarks>
public static class AlertCommandParser
{
    /// <summary>
    /// Past this many characters, the input is rejected outright rather than tokenised -- a pipe
    /// message this long is already malformed, not merely a command with many groups.
    /// </summary>
    private const int MaxInputLength = 256;

    private const string DurationKey = "duration";

    private const int MinTileCount = 1;

    private const int MaxTileCountPerKind = 16;

    private const int MaxTotalTiles = 16;

    private const int MinDurationSeconds = 1;

    private const int MaxDurationSeconds = 60;

    private const int DefaultDurationSeconds = 5;

    public static AlertCommandParseResult Parse(string? input)
    {
        if (input is null)
        {
            return AlertCommandParseResult.Fail("no command was given");
        }

        if (input.Length > MaxInputLength)
        {
            return AlertCommandParseResult.Fail(
                $"command is {input.Length} characters long, past the {MaxInputLength}-character limit");
        }

        var groups = new List<AlertGroup>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var durationSeconds = DefaultDurationSeconds;

        // Any run of whitespace separates tokens; RemoveEmptyEntries also throws away leading,
        // trailing and doubled-up separators for free.
        foreach (var token in input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = token.IndexOf(':');
            if (separatorIndex <= 0 || separatorIndex == token.Length - 1)
            {
                return AlertCommandParseResult.Fail($"'{token}' is not a 'key:count' token");
            }

            var key = token[..separatorIndex];
            var value = token[(separatorIndex + 1)..];
            var isDuration = key.Equals(DurationKey, StringComparison.OrdinalIgnoreCase);
            AlertKind kind = default;

            if (!isDuration && !TryParseKind(key, out kind))
            {
                return AlertCommandParseResult.Fail($"'{token}' names an unknown key '{key}'");
            }

            if (!seenKeys.Add(key.ToLowerInvariant()))
            {
                return AlertCommandParseResult.Fail($"'{key}' is repeated in '{token}'");
            }

            // NumberStyles.None rejects a leading sign, a decimal point and thousands separators --
            // "count" is a plain decimal integer, never "+1", "-1" or "1.0".
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                return AlertCommandParseResult.Fail($"'{token}' does not carry a whole number");
            }

            if (isDuration)
            {
                if (number is < MinDurationSeconds or > MaxDurationSeconds)
                {
                    return AlertCommandParseResult.Fail(
                        $"'{token}' must be {MinDurationSeconds}..{MaxDurationSeconds} seconds");
                }

                durationSeconds = number;
                continue;
            }

            if (number is < MinTileCount or > MaxTileCountPerKind)
            {
                return AlertCommandParseResult.Fail(
                    $"'{token}' must be {MinTileCount}..{MaxTileCountPerKind}");
            }

            groups.Add(new AlertGroup(kind, number));
        }

        if (groups.Count == 0)
        {
            return AlertCommandParseResult.Fail("at least one 'warning:N' or 'failed:N' group is required");
        }

        var totalTiles = groups.Sum(group => group.Count);
        if (totalTiles > MaxTotalTiles)
        {
            return AlertCommandParseResult.Fail(
                $"{totalTiles} tiles were requested, past the {MaxTotalTiles}-tile limit");
        }

        return AlertCommandParseResult.Ok(new AlertCommand(groups, TimeSpan.FromSeconds(durationSeconds)));
    }

    private static bool TryParseKind(string key, out AlertKind kind)
    {
        if (key.Equals("warning", StringComparison.OrdinalIgnoreCase))
        {
            kind = AlertKind.Warning;
            return true;
        }

        if (key.Equals("failed", StringComparison.OrdinalIgnoreCase))
        {
            kind = AlertKind.Failed;
            return true;
        }

        kind = default;
        return false;
    }
}
