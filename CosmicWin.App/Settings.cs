namespace CosmicWin.App;

/// <summary>
/// The handful of preferences CosmicWin keeps between runs.
/// </summary>
/// <param name="FocusBorder">
/// Whether CosmicWin draws its own thicker focus border. Turned off, the active window keeps only
/// the thin one DWM draws for itself -- which is a legitimate preference, not a degraded mode.
/// </param>
/// <remarks>
/// <para>
/// Text, not JSON or the registry, and for the same reason <c>exceptions.conf</c> is text: this is
/// a file a person is expected to open in an editor. It parses the way that one does, too -- blank
/// lines and <c>#</c> comments ignored, an unreadable line SKIPPED rather than thrown on, so a typo
/// costs one setting instead of blocking startup.
/// </para>
/// <para>
/// Parsing takes raw text rather than a path, keeping the format testable with no disk at all;
/// <see cref="SettingsFile"/> owns the reading and writing.
/// </para>
/// </remarks>
public sealed record Settings(bool FocusBorder)
{
    /// <summary>
    /// What CosmicWin does when nobody has said otherwise. The border is ON: a settings file that
    /// has never been written must not turn a feature off.
    /// </summary>
    public static Settings Default { get; } = new(FocusBorder: true);

    private const string FocusBorderKey = "focus-border";

    /// <summary>
    /// Reads <paramref name="content"/> into settings, keeping the default for anything it does not
    /// state and anything it states unreadably.
    /// </summary>
    /// <remarks>
    /// The LAST assignment of a key wins. A file appended to twice is a thing that happens, and
    /// reading it as its most recent line is the only answer that matches what an editor shows.
    /// </remarks>
    public static Settings Parse(string content)
    {
        var focusBorder = Default.FocusBorder;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();

            if (!key.Equals(FocusBorderKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Only a value we RECOGNISE moves the setting. "focus-border = perhaps" is a typo, and
            // guessing which way the user meant it is worse than leaving the default alone.
            if (TryReadFlag(value, out var flag))
            {
                focusBorder = flag;
            }
        }

        return new Settings(focusBorder);
    }

    /// <summary>The file this instance would be written as, comment and all.</summary>
    public string Serialize() =>
        $"""
         # CosmicWin settings. Edited by hand or by the tray menu.
         # {FocusBorderKey}: on to draw CosmicWin's thicker focus border, off to keep only Windows' own.
         {FocusBorderKey} = {(FocusBorder ? "on" : "off")}

         """;

    /// <summary>
    /// Accepts the three spellings a person actually types. Deliberately NOT
    /// <c>bool.TryParse</c> alone, which knows "true" and "false" and would reject "on" -- the word
    /// this file's own comment tells the user to write.
    /// </summary>
    private static bool TryReadFlag(string value, out bool flag)
    {
        switch (value.ToLowerInvariant())
        {
            case "on" or "true" or "1":
                flag = true;
                return true;
            case "off" or "false" or "0":
                flag = false;
                return true;
            default:
                flag = false;
                return false;
        }
    }
}
