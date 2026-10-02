using CosmicWin.Interop;

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
/// <param name="Tiling">
/// Whether CosmicWin lays windows out at all. Turned off, windows are left exactly where their
/// applications put them and the layout chords go quiet, while the virtual-desktop chords keep
/// working -- which is a legitimate way to use this app, not a degraded one.
/// </param>
/// <param name="VideoWallpaperPath">
/// The absolute path to the MP4 CosmicWin loops as the desktop wallpaper, or <see langword="null"/>
/// for none -- which is what every machine has before anyone has picked a video, and a legitimate
/// way to run this app forever, not a half-configured one.
/// </param>
/// <param name="AlertsEnabled">
/// Whether live alert commands are accepted over the named pipe and the HTTP alerts route, and drawn
/// over the wallpaper. Settings key <c>alerts</c> (legacy: <c>alerts-enabled</c>).
/// </param>
/// <param name="HttpServerEnabled">
/// The ONE switch for the local HTTP server (settings key <c>http-server</c>, legacy:
/// <c>alert-http</c>). When on, every route is served -- <c>/v1/alerts</c> and
/// <c>/v1/wallpaper/video</c>.
/// ON by default (maintainer's decision, S9): loopback-only (127.0.0.1 / localhost, never reachable
/// over the network) and gated by a bearer token nothing outside this machine can read.
/// </param>
/// <param name="HttpServerPort">
/// The loopback TCP port the HTTP server listens on when <see cref="HttpServerEnabled"/> is on
/// (settings key <c>http-server-port</c>, legacy: <c>alert-http-port</c>). Defaults to
/// <see cref="AlertHttpProtocol.DefaultPort"/>, the same constant the server itself falls back to.
/// </param>
/// <param name="Gap">
/// Whole pixels of space CosmicWin draws around and between tiled windows, and around and between an
/// alert's tiles -- the SAME value drives both (<c>TreeArranger.Gap</c>). Whole pixels 0-64;
/// defaults to <see cref="TreeArranger.DefaultGap"/>, the value <c>TreeArranger.Gap</c> was hard-set
/// to before this was a settings key at all.
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
public sealed record Settings(bool FocusBorder, uint? BorderColor = null, bool Tiling = true,
    string? VideoWallpaperPath = null, bool AlertsEnabled = true, bool HttpServerEnabled = true,
    int HttpServerPort = AlertHttpProtocol.DefaultPort,
    int Gap = TreeArranger.DefaultGap)
{
    /// <summary>
    /// What CosmicWin does when nobody has said otherwise. The border is ON: a settings file that
    /// has never been written must not turn a feature off. Its colour is the system accent, which
    /// is the one colour guaranteed to look deliberate on a desktop nobody has configured. And
    /// tiling is ON, for a stronger version of the same reason -- it is what the app is for.
    /// </summary>
    public static Settings Default { get; } = new(FocusBorder: true);

    private const string FocusBorderKey = "focus-border";

    private const string BorderColorKey = "border-color";

    private const string TilingKey = "tiling";

    private const string VideoWallpaperPathKey = "video-wallpaper-path";

    private const string AlertsKey = "alerts";

    private const string LegacyAlertsKey = "alerts-enabled";

    private const string HttpServerKey = "http-server";

    private const string LegacyHttpServerKey = "alert-http";

    private const string HttpServerPortKey = "http-server-port";

    private const string LegacyHttpServerPortKey = "alert-http-port";

    private const string GapKey = "gap";

    /// <summary>The value that hands the colour back to Windows, so the tray has a way home.</summary>
    private const string AccentValue = "accent";

    /// <summary>
    /// Reads <paramref name="content"/> into settings, keeping the default for anything it does not
    /// state and anything it states unreadably.
    /// </summary>
    /// <remarks>
    /// The LAST assignment of a key wins. A file appended to twice is a thing that happens, and
    /// reading it as its most recent line is the only answer that matches what an editor shows.
    /// Legacy key names (<c>alerts-enabled</c>, <c>alert-http</c>, <c>alert-http-port</c>) are still
    /// read, but when a file carries both a legacy key and its replacement the NEW key wins whatever
    /// the line order. <c>video-wallpaper-http</c>, <c>wallpaper-scene-http</c>, <c>mini-position</c>,
    /// <c>mini-corner</c>, <c>wallpaper-mode</c>, <c>wallpaper-scene</c> and <c>wallpaper-fps</c> no
    /// longer exist: they are skipped like any unknown key. There is no wallpaper mode any more -- the
    /// video plays whenever <c>video-wallpaper-path</c> is set.
    /// </remarks>
    public static Settings Parse(string content)
    {
        var focusBorder = Default.FocusBorder;
        var borderColor = Default.BorderColor;
        var tiling = Default.Tiling;
        var videoWallpaperPath = Default.VideoWallpaperPath;
        bool? alerts = null, legacyAlerts = null, httpServer = null, legacyHttpServer = null;
        int? httpServerPort = null, legacyHttpServerPort = null;
        var gap = Default.Gap;

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
            else if (key.Equals(TilingKey, StringComparison.OrdinalIgnoreCase)
                && TryReadFlag(value, out var tilingFlag))
            {
                tiling = tilingFlag;
            }
            else if (key.Equals(VideoWallpaperPathKey, StringComparison.OrdinalIgnoreCase)
                && value.Length > 0)
            {
                videoWallpaperPath = value;
            }
            else if (key.Equals(AlertsKey, StringComparison.OrdinalIgnoreCase)
                && TryReadFlag(value, out var alertsFlag))
            {
                alerts = alertsFlag;
            }
            else if (key.Equals(LegacyAlertsKey, StringComparison.OrdinalIgnoreCase)
                && TryReadFlag(value, out var legacyAlertsFlag))
            {
                legacyAlerts = legacyAlertsFlag;
            }
            else if (key.Equals(HttpServerKey, StringComparison.OrdinalIgnoreCase)
                && TryReadFlag(value, out var httpServerFlag))
            {
                httpServer = httpServerFlag;
            }
            else if (key.Equals(LegacyHttpServerKey, StringComparison.OrdinalIgnoreCase)
                && TryReadFlag(value, out var legacyHttpServerFlag))
            {
                legacyHttpServer = legacyHttpServerFlag;
            }
            else if (key.Equals(HttpServerPortKey, StringComparison.OrdinalIgnoreCase)
                && TryReadPort(value, out var port))
            {
                httpServerPort = port;
            }
            else if (key.Equals(LegacyHttpServerPortKey, StringComparison.OrdinalIgnoreCase)
                && TryReadPort(value, out var legacyPort))
            {
                legacyHttpServerPort = legacyPort;
            }
            else if (key.Equals(GapKey, StringComparison.OrdinalIgnoreCase)
                && TryReadGap(value, out var gapValue))
            {
                gap = gapValue;
            }
            // video-wallpaper-http and wallpaper-scene-http (per-route toggles), mini-position /
            // mini-corner (the retired mini scene window) and wallpaper-mode / wallpaper-scene /
            // wallpaper-fps (the retired html wallpaper) no longer exist. Deliberately no branch --
            // the line is skipped like any other unknown key.
        }

        return new Settings(focusBorder, borderColor, tiling, videoWallpaperPath,
            alerts ?? legacyAlerts ?? Default.AlertsEnabled,
            httpServer ?? legacyHttpServer ?? Default.HttpServerEnabled,
            httpServerPort ?? legacyHttpServerPort ?? Default.HttpServerPort,
            gap);
    }

    /// <summary>The file this instance would be written as, comment and all.</summary>
    public string Serialize() =>
        $"""
         # CosmicWin settings. Edited by hand or by the tray menu.
         # {FocusBorderKey}: on to draw CosmicWin's thicker focus border, off to keep only Windows' own.
         {FocusBorderKey} = {(FocusBorder ? "on" : "off")}

         # {BorderColorKey}: #RRGGBB, or `{AccentValue}` to follow Windows' own accent colour.
         {BorderColorKey} = {(BorderColor is { } rgb ? $"#{rgb:X6}" : AccentValue)}

         # {TilingKey}: on to lay windows out, off to leave them where they open. Off, the
         # virtual-desktop chords keep working and only the layout ones go quiet.
         {TilingKey} = {(Tiling ? "on" : "off")}

         # {VideoWallpaperPathKey}: absolute path to an MP4 file to loop as the desktop wallpaper,
         # or blank for none.
         {VideoWallpaperPathKey} = {VideoWallpaperPath ?? ""}

         # {AlertsKey}: on to accept live alert commands (named pipe and HTTP), off to ignore them.
         {AlertsKey} = {(AlertsEnabled ? "on" : "off")}

         # {HttpServerKey}: on (default) to run the local HTTP server that serves every route
         # (alerts, video wallpaper). Loopback-only (127.0.0.1 / localhost, never
         # reachable over the network); its bearer token lives in
         # %LOCALAPPDATA%\CosmicWin\alert-http.token, created the first time the server starts.
         # Off to leave the port closed.
         {HttpServerKey} = {(HttpServerEnabled ? "on" : "off")}

         # {HttpServerPortKey}: the loopback TCP port the HTTP server listens on when {HttpServerKey} is on.
         {HttpServerPortKey} = {HttpServerPort.ToString(System.Globalization.CultureInfo.InvariantCulture)}

         # {GapKey}: whole pixels of space around and between tiled windows, and around and between
         # an alert's tiles -- the SAME value drives both. 0-64, default {TreeArranger.DefaultGap}.
         {GapKey} = {Gap.ToString(System.Globalization.CultureInfo.InvariantCulture)}

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

    /// <summary>
    /// Reads a TCP port, 1-65535. Same rule as every other key: anything outside that range, or not
    /// a whole number at all, keeps the default rather than opening a port that makes no sense.
    /// </summary>
    private static bool TryReadPort(string value, out int port)
    {
        if (int.TryParse(value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out port)
            && port is >= 1 and <= 65535)
        {
            return true;
        }

        port = 0;
        return false;
    }

    /// <summary>
    /// Reads a whole-pixel gap, 0-64. Same rule as every other key: anything outside that range, or
    /// not a whole number at all, keeps the default rather than drawing a gap nobody asked for.
    /// </summary>
    private static bool TryReadGap(string value, out int gap)
    {
        if (int.TryParse(value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out gap)
            && gap is >= 0 and <= 64)
        {
            return true;
        }

        gap = 0;
        return false;
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
