namespace CosmicWin.App;

/// <summary>
/// The handful of preferences CosmicWin keeps between runs.
/// </summary>
/// <param name="FocusBorder">
/// Whether CosmicWin draws its own thicker focus border. Turned off, the active window keeps only
/// the thin one DWM draws for itself -- which is a legitimate preference, not a degraded mode.
/// </param>
/// <param name="BorderColor">
/// The colour of that border as <c>0xRRGGBB</c>, or <see langword="null"/> to follow Windows' own
/// accent -- which is what it drew before it could be configured at all.
/// </param>
/// <remarks>
/// <para>
/// The colour is a plain <c>uint</c> rather than a WPF <c>Color</c> on purpose. This type is the
/// FORMAT, parsed and serialised with no disk and no UI framework anywhere near it, and the moment
/// it names a presentation type every test of the format has to drag one in.
/// </para>
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
public sealed record Settings(bool FocusBorder, uint? BorderColor = null)
{
    /// <summary>
    /// What CosmicWin does when nobody has said otherwise. The border is ON: a settings file that
    /// has never been written must not turn a feature off. Its colour is the system accent, which
    /// is the one colour guaranteed to look deliberate on a desktop nobody has configured.
    /// </summary>
    public static Settings Default { get; } = new(FocusBorder: true);

    private const string FocusBorderKey = "focus-border";

    private const string BorderColorKey = "border-color";

    /// <summary>The value that hands the colour back to Windows, so the tray has a way home.</summary>
    private const string AccentValue = "accent";

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
        var borderColor = Default.BorderColor;

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

            // Only a value we RECOGNISE moves the setting. "focus-border = perhaps" is a typo, and
            // guessing which way the user meant it is worse than leaving the default alone. The
            // colour follows the same rule, and it costs only itself: an unreadable colour must not
            // take the flag on the line above it down with it.
            if (key.Equals(FocusBorderKey, StringComparison.OrdinalIgnoreCase))
            {
                if (TryReadFlag(value, out var flag))
                {
                    focusBorder = flag;
                }
            }
            else if (key.Equals(BorderColorKey, StringComparison.OrdinalIgnoreCase)
                && TryReadColor(value, out var colour))
            {
                borderColor = colour;
            }
        }

        return new Settings(focusBorder, borderColor);
    }

    /// <summary>The file this instance would be written as, comment and all.</summary>
    public string Serialize() =>
        $"""
         # CosmicWin settings. Edited by hand or by the tray menu.
         # {FocusBorderKey}: on to draw CosmicWin's thicker focus border, off to keep only Windows' own.
         {FocusBorderKey} = {(FocusBorder ? "on" : "off")}

         # {BorderColorKey}: #RRGGBB, or `{AccentValue}` to follow Windows' own accent colour.
         {BorderColorKey} = {(BorderColor is { } rgb ? $"#{rgb:X6}" : AccentValue)}

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

    /// <summary>
    /// Reads <c>#RRGGBB</c>, <c>#RGB</c>, the same two without the hash, or the word
    /// <c>accent</c> -- which reads as a colour of <see langword="null"/>, not as a failure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The three-digit form doubles each digit, exactly as CSS does, so <c>#f80</c> is
    /// <c>#ff8800</c>. Half the people who type a colour from memory type that one, and reading it
    /// as a near-miss nobody can see is wrong would be worse than rejecting it.
    /// </para>
    /// <para>
    /// The hash is optional because a settings file is not CSS and nobody should lose a colour to
    /// forgetting it. Length is checked BEFORE parsing: <c>Convert.ToUInt32</c> happily accepts five
    /// digits and would answer with a colour the user never typed.
    /// </para>
    /// </remarks>
    private static bool TryReadColor(string value, out uint? colour)
    {
        colour = null;

        var text = value.Trim();
        if (text.Equals(AccentValue, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var digits = text.StartsWith('#') ? text[1..] : text;
        if (digits.Length is not (3 or 6) || !uint.TryParse(
                digits, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        colour = digits.Length == 6 ? parsed : Expand(parsed);
        return true;
    }

    /// <summary>Doubles each of the three nibbles: <c>0xF80</c> becomes <c>0xFF8800</c>.</summary>
    private static uint Expand(uint shortForm)
    {
        var red = (shortForm >> 8) & 0xF;
        var green = (shortForm >> 4) & 0xF;
        var blue = shortForm & 0xF;

        return ((red * 0x11) << 16) | ((green * 0x11) << 8) | (blue * 0x11);
    }
}
