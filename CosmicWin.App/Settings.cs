using CosmicWin.Interop;

namespace CosmicWin.App;

/// <summary>
/// Which renderer draws the desktop wallpaper. <see cref="Video"/> loops the MP4 at
/// <see cref="Settings.VideoWallpaperPath"/>, exactly as CosmicWin always has. <see cref="Html"/>
/// attaches the same wallpaper host (see <c>AppComposition.Wire</c>'s <c>AttachHtmlWallpaper</c>)
/// with no video ever playing, purely so an animated HTML scene page can be composited over it,
/// driven by the same alert commands the video layer answers -- CosmicWin's default renderer since
/// S8 (wallpaper-scene-http-endpoint, 2026-09-27), after it first shipped as D3's demo. See
/// <c>odd/tasks/html-wallpaper-demo.md</c> for how it was built and
/// <c>odd/tasks/wallpaper-scene-http-endpoint.md</c> for S8's graduation decision.
/// </summary>
public enum WallpaperMode
{
    Video,
    Html,

    /// <summary>
    /// No wallpaper host and no video: a small, always-on-top, click-through scene window sits in
    /// <see cref="Settings.MiniPosition"/> of the work area, showing the same HTML scene the
    /// wallpaper would; the desktop background stays as Windows has it. Serialized as
    /// <c>html-mini</c>; the legacy spelling <c>mini</c> is still read.
    /// </summary>
    HtmlMini,
}

/// <summary>The work-area position the mini scene window sits in: four corners and four side midpoints, ordered clockwise.</summary>
public enum MiniPosition
{
    TopLeft,
    TopCenter,
    TopRight,
    RightCenter,
    BottomRight,
    BottomCenter,
    BottomLeft,
    LeftCenter,
}

/// <summary>
/// D6d (html-wallpaper-demo, 2026-09-27): which of the four ported scenes the
/// html wallpaper (<see cref="WallpaperMode.Html"/>) shows. Each member maps to exactly one fixed
/// folder name under <c>CosmicWin.App/Wallpaper/Web/</c> (see
/// <c>WebViewAlertLayerController.SceneFolderName</c>) -- a closed, compile-time-fixed set, so no
/// text from this settings file (or anywhere else) can ever reach the scene page's URL as a raw,
/// unvalidated folder segment. <see cref="Processing"/> is the default: today's only shipped scene
/// before D6d, unchanged for anyone who never edits this key.
/// </summary>
public enum WallpaperScene
{
    Processing,
    Explorer,
    Idle,
    Raphael,
}

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
/// <c>alert-http</c>). When on, every route is served -- <c>/v1/alerts</c>,
/// <c>/v1/wallpaper/video</c> and <c>/v1/wallpaper/scene</c> -- each keeping its own mode guard.
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
/// <param name="WallpaperMode">
/// S8 (wallpaper-scene-http-endpoint, 2026-09-27): <see cref="CosmicWin.App.WallpaperMode.Html"/>
/// unless the file says otherwise -- the html wallpaper (first built as D3's demo, html-wallpaper-
/// demo) graduated to CosmicWin's default renderer. <see cref="CosmicWin.App.WallpaperMode.Video"/>
/// remains fully supported for anyone who sets <c>wallpaper-mode = video</c>.
/// </param>
/// <param name="WallpaperScene">
/// D6d (html-wallpaper-demo, 2026-09-27): which scene the html wallpaper shows, one of the four
/// fixed <see cref="CosmicWin.App.WallpaperScene"/> members. Defaults to <see
/// cref="CosmicWin.App.WallpaperScene.Processing"/> -- the only scene that shipped before D6d.
/// </param>
/// <param name="WallpaperFps">
/// D6d (html-wallpaper-demo, 2026-09-27): caps how many times per second the html wallpaper's scene
/// and alert overlay draw, 30 or 60. Defaults to 60. Before D6d the scene had no cap at all and drew
/// on every real animation frame -- i.e. at the display's own refresh rate (over 60fps on any display
/// faster than 60Hz) -- so 60 is a REDUCTION for anyone on such a display, not a no-op default.
/// </param>
/// <param name="MiniPosition">
/// Where in the work area the <see cref="WallpaperMode.HtmlMini"/> window sits (settings key
/// <c>mini-position</c>, legacy: <c>mini-corner</c>). Defaults to top-right.
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
    int Gap = TreeArranger.DefaultGap, WallpaperMode WallpaperMode = WallpaperMode.Html,
    WallpaperScene WallpaperScene = WallpaperScene.Processing, int WallpaperFps = 60,
    MiniPosition MiniPosition = MiniPosition.TopRight)
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

    /// <summary>D3 (html-wallpaper-demo): see <see cref="CosmicWin.App.WallpaperMode"/>.</summary>
    private const string WallpaperModeKey = "wallpaper-mode";

    private const string WallpaperModeVideoValue = "video";

    private const string WallpaperModeHtmlValue = "html";

    private const string WallpaperModeHtmlMiniValue = "html-mini";

    /// <summary>Legacy spelling of <see cref="WallpaperModeHtmlMiniValue"/>, read but never written.</summary>
    private const string LegacyWallpaperModeMiniValue = "mini";

    /// <summary>See <see cref="CosmicWin.App.MiniPosition"/>.</summary>
    private const string MiniPositionKey = "mini-position";

    private const string LegacyMiniPositionKey = "mini-corner";

    private const string MiniPositionTopLeftValue = "top-left";

    private const string MiniPositionTopRightValue = "top-right";

    private const string MiniPositionBottomLeftValue = "bottom-left";

    private const string MiniPositionBottomRightValue = "bottom-right";
    private const string MiniPositionTopCenterValue = "top-center";
    private const string MiniPositionRightCenterValue = "right-center";
    private const string MiniPositionBottomCenterValue = "bottom-center";
    private const string MiniPositionLeftCenterValue = "left-center";

    /// <summary>D6d (html-wallpaper-demo): see <see cref="CosmicWin.App.WallpaperScene"/>.</summary>
    private const string WallpaperSceneKey = "wallpaper-scene";

    private const string WallpaperSceneProcessingValue = "processing";

    private const string WallpaperSceneExplorerValue = "explorer";

    private const string WallpaperSceneIdleValue = "idle";

    private const string WallpaperSceneRaphaelValue = "raphael";

    /// <summary>D6d (html-wallpaper-demo): caps the html wallpaper's own frame rate.</summary>
    private const string WallpaperFpsKey = "wallpaper-fps";

    private const string WallpaperFps30Value = "30";

    private const string WallpaperFps60Value = "60";

    /// <summary>The value that hands the colour back to Windows, so the tray has a way home.</summary>
    private const string AccentValue = "accent";

    /// <summary>
    /// Reads <paramref name="content"/> into settings, keeping the default for anything it does not
    /// state and anything it states unreadably.
    /// </summary>
    /// <remarks>
    /// The LAST assignment of a key wins. A file appended to twice is a thing that happens, and
    /// reading it as its most recent line is the only answer that matches what an editor shows.
    /// Legacy key names (<c>alerts-enabled</c>, <c>alert-http</c>, <c>alert-http-port</c>,
    /// <c>mini-corner</c>) are still read, but when a file carries both a legacy key and its
    /// replacement the NEW key wins whatever the line order. <c>video-wallpaper-http</c> and
    /// <c>wallpaper-scene-http</c> no longer exist: they are skipped like any unknown key.
    /// </remarks>
    public static Settings Parse(string content)
    {
        var focusBorder = Default.FocusBorder;
        var borderColor = Default.BorderColor;
        var tiling = Default.Tiling;
        var videoWallpaperPath = Default.VideoWallpaperPath;
        bool? alerts = null, legacyAlerts = null, httpServer = null, legacyHttpServer = null;
        int? httpServerPort = null, legacyHttpServerPort = null;
        MiniPosition? miniPosition = null, legacyMiniPosition = null;
        var gap = Default.Gap;
        var wallpaperMode = Default.WallpaperMode;
        var wallpaperScene = Default.WallpaperScene;
        var wallpaperFps = Default.WallpaperFps;

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
            else if (key.Equals(WallpaperModeKey, StringComparison.OrdinalIgnoreCase)
                && TryReadWallpaperMode(value, out var wallpaperModeValue))
            {
                wallpaperMode = wallpaperModeValue;
            }
            else if (key.Equals(WallpaperSceneKey, StringComparison.OrdinalIgnoreCase)
                && TryReadWallpaperScene(value, out var wallpaperSceneValue))
            {
                wallpaperScene = wallpaperSceneValue;
            }
            else if (key.Equals(WallpaperFpsKey, StringComparison.OrdinalIgnoreCase)
                && TryReadWallpaperFps(value, out var wallpaperFpsValue))
            {
                wallpaperFps = wallpaperFpsValue;
            }
            else if (key.Equals(MiniPositionKey, StringComparison.OrdinalIgnoreCase)
                && TryReadMiniPosition(value, out var miniPositionValue))
            {
                miniPosition = miniPositionValue;
            }
            else if (key.Equals(LegacyMiniPositionKey, StringComparison.OrdinalIgnoreCase)
                && TryReadMiniPosition(value, out var legacyMiniPositionValue))
            {
                legacyMiniPosition = legacyMiniPositionValue;
            }
            // video-wallpaper-http and wallpaper-scene-http: per-route toggles that no longer exist.
            // Deliberately no branch -- the line is skipped like any other unknown key.
        }

        return new Settings(focusBorder, borderColor, tiling, videoWallpaperPath,
            alerts ?? legacyAlerts ?? Default.AlertsEnabled,
            httpServer ?? legacyHttpServer ?? Default.HttpServerEnabled,
            httpServerPort ?? legacyHttpServerPort ?? Default.HttpServerPort,
            gap, wallpaperMode, wallpaperScene, wallpaperFps,
            miniPosition ?? legacyMiniPosition ?? Default.MiniPosition);
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
         # (alerts, video wallpaper, wallpaper scene). Loopback-only (127.0.0.1 / localhost, never
         # reachable over the network); its bearer token lives in
         # %LOCALAPPDATA%\CosmicWin\alert-http.token, created the first time the server starts.
         # Off to leave the port closed.
         {HttpServerKey} = {(HttpServerEnabled ? "on" : "off")}

         # {HttpServerPortKey}: the loopback TCP port the HTTP server listens on when {HttpServerKey} is on.
         {HttpServerPortKey} = {HttpServerPort.ToString(System.Globalization.CultureInfo.InvariantCulture)}

         # {GapKey}: whole pixels of space around and between tiled windows, and around and between
         # an alert's tiles -- the SAME value drives both. 0-64, default {TreeArranger.DefaultGap}.
         {GapKey} = {Gap.ToString(System.Globalization.CultureInfo.InvariantCulture)}

         # {WallpaperModeKey}: `{WallpaperModeHtmlValue}` (default) shows an animated HTML scene as the
         # desktop wallpaper; `{WallpaperModeVideoValue}` plays the configured video instead, with no
         # HTML scene involved; `{WallpaperModeHtmlMiniValue}` leaves the wallpaper alone and shows a small
         # always-on-top scene window ({MiniPositionKey}).
         {WallpaperModeKey} = {WallpaperModeValue(WallpaperMode)}

         # {WallpaperSceneKey}: which scene the html wallpaper ({WallpaperModeKey} = {WallpaperModeHtmlValue})
         # shows: `{WallpaperSceneProcessingValue}` (default), `{WallpaperSceneExplorerValue}`,
         # `{WallpaperSceneIdleValue}`, or `{WallpaperSceneRaphaelValue}`.
         {WallpaperSceneKey} = {WallpaperSceneValue(WallpaperScene)}

         # {WallpaperFpsKey}: caps the html wallpaper's own frame rate: `{WallpaperFps30Value}` or
         # `{WallpaperFps60Value}` (default). Before this setting existed the scene drew uncapped, at
         # the display's own refresh rate.
         {WallpaperFpsKey} = {WallpaperFps.ToString(System.Globalization.CultureInfo.InvariantCulture)}

         # {MiniPositionKey}: where in the work area the `{WallpaperModeHtmlMiniValue}` window sits: a corner
         # (`{MiniPositionTopLeftValue}`, `{MiniPositionTopRightValue}` (default), `{MiniPositionBottomLeftValue}`,
         # `{MiniPositionBottomRightValue}`) or a side midpoint (`{MiniPositionTopCenterValue}`,
         # `{MiniPositionRightCenterValue}`, `{MiniPositionBottomCenterValue}`, `{MiniPositionLeftCenterValue}`).
         {MiniPositionKey} = {MiniPositionValue(MiniPosition)}

         """;

    /// <summary>Maps a <see cref="CosmicWin.App.WallpaperMode"/> to the exact literal <see cref="Serialize"/> writes for it.</summary>
    private static string WallpaperModeValue(WallpaperMode mode) => mode switch
    {
        WallpaperMode.Html => WallpaperModeHtmlValue,
        WallpaperMode.HtmlMini => WallpaperModeHtmlMiniValue,
        _ => WallpaperModeVideoValue,
    };

    /// <summary>Maps a <see cref="CosmicWin.App.MiniPosition"/> to the exact literal <see cref="Serialize"/> writes for it.</summary>
    private static string MiniPositionValue(MiniPosition corner) => corner switch
    {
        MiniPosition.TopLeft => MiniPositionTopLeftValue,
        MiniPosition.TopRight => MiniPositionTopRightValue,
        MiniPosition.BottomLeft => MiniPositionBottomLeftValue,
        MiniPosition.TopCenter => MiniPositionTopCenterValue,
        MiniPosition.RightCenter => MiniPositionRightCenterValue,
        MiniPosition.BottomCenter => MiniPositionBottomCenterValue,
        MiniPosition.LeftCenter => MiniPositionLeftCenterValue,
        _ => MiniPositionBottomRightValue,
    };

    /// <summary>Maps a <see cref="CosmicWin.App.WallpaperScene"/> to the exact literal <see cref="Serialize"/> writes for it.</summary>
    private static string WallpaperSceneValue(WallpaperScene scene) => scene switch
    {
        WallpaperScene.Explorer => WallpaperSceneExplorerValue,
        WallpaperScene.Idle => WallpaperSceneIdleValue,
        WallpaperScene.Raphael => WallpaperSceneRaphaelValue,
        _ => WallpaperSceneProcessingValue,
    };

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

    /// <summary>
    /// D3 (html-wallpaper-demo): reads <c>video</c>, <c>html</c>, <c>html-mini</c> or the legacy <c>mini</c>, case-insensitively. Same rule as
    /// every other key: anything else keeps the default rather than guessing.
    /// </summary>
    private static bool TryReadWallpaperMode(string value, out WallpaperMode mode)
    {
        switch (value.ToLowerInvariant())
        {
            case WallpaperModeVideoValue:
                mode = WallpaperMode.Video;
                return true;
            case WallpaperModeHtmlValue:
                mode = WallpaperMode.Html;
                return true;
            case WallpaperModeHtmlMiniValue or LegacyWallpaperModeMiniValue:
                mode = WallpaperMode.HtmlMini;
                return true;
            default:
                mode = WallpaperMode.Video;
                return false;
        }
    }

    /// <summary>
    /// Reads one of the four fixed corner names, case-insensitively. Same rule as every other key:
    /// anything else keeps the default (top-right) rather than guessing.
    /// </summary>
    private static bool TryReadMiniPosition(string value, out MiniPosition corner)
    {
        switch (value.ToLowerInvariant())
        {
            case MiniPositionTopLeftValue:
                corner = MiniPosition.TopLeft;
                return true;
            case MiniPositionTopRightValue:
                corner = MiniPosition.TopRight;
                return true;
            case MiniPositionBottomLeftValue:
                corner = MiniPosition.BottomLeft;
                return true;
            case MiniPositionBottomRightValue:
                corner = MiniPosition.BottomRight;
                return true;
            case MiniPositionTopCenterValue:
                corner = MiniPosition.TopCenter;
                return true;
            case MiniPositionRightCenterValue:
                corner = MiniPosition.RightCenter;
                return true;
            case MiniPositionBottomCenterValue:
                corner = MiniPosition.BottomCenter;
                return true;
            case MiniPositionLeftCenterValue:
                corner = MiniPosition.LeftCenter;
                return true;
            default:
                corner = MiniPosition.TopRight;
                return false;
        }
    }

    /// <summary>
    /// D6d (html-wallpaper-demo): reads one of the four fixed scene names, case-insensitively. Same
    /// rule as every other key: anything else keeps the default rather than guessing -- and, since
    /// <see cref="WallpaperScene"/> is a closed enum, this is also the ONLY place raw settings text
    /// ever turns into a scene value at all.
    /// </summary>
    private static bool TryReadWallpaperScene(string value, out WallpaperScene scene)
    {
        switch (value.ToLowerInvariant())
        {
            case WallpaperSceneProcessingValue:
                scene = WallpaperScene.Processing;
                return true;
            case WallpaperSceneExplorerValue:
                scene = WallpaperScene.Explorer;
                return true;
            case WallpaperSceneIdleValue:
                scene = WallpaperScene.Idle;
                return true;
            case WallpaperSceneRaphaelValue:
                scene = WallpaperScene.Raphael;
                return true;
            default:
                scene = WallpaperScene.Processing;
                return false;
        }
    }

    /// <summary>
    /// D6d (html-wallpaper-demo): reads exactly <c>30</c> or <c>60</c>. Same rule as every other key:
    /// anything else -- including a number that merely isn't one of those two -- keeps the default
    /// (60) rather than guessing. 60 is a CAP, not the historical behaviour: before this setting
    /// existed the scene drew uncapped, at the display's own refresh rate.
    /// </summary>
    private static bool TryReadWallpaperFps(string value, out int fps)
    {
        switch (value)
        {
            case WallpaperFps30Value:
                fps = 30;
                return true;
            case WallpaperFps60Value:
                fps = 60;
                return true;
            default:
                fps = 60;
                return false;
        }
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
