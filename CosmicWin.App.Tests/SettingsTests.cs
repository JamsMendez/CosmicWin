using CosmicWin.Interop;

namespace CosmicWin.App.Tests;

/// <summary>
/// The settings file's text format, tested as the pure function it is.
/// </summary>
/// <remarks>
/// Separate from <see cref="SettingsFileTests"/> on purpose, and for the same reason
/// <see cref="CosmicWin.Layout.Filters.ExceptionListLoader"/> takes text rather than a path: the
/// FORMAT is where a typo silently changes behaviour, and it deserves facts that never touch a disk.
/// </remarks>
public sealed class SettingsTests
{
    /// <summary>
    /// The border is ON unless the file says otherwise. A settings file that has never been written
    /// must not turn a feature off.
    /// </summary>
    [Fact]
    public void EmptyContent_LeavesEveryDefaultInPlace()
    {
        var settings = Settings.Parse(string.Empty);

        Assert.True(settings.FocusBorder);
        Assert.True(settings.AlertsEnabled);
        Assert.Equal(Settings.Default, settings);
    }

    [Theory]
    [InlineData("focus-border = off")]
    [InlineData("focus-border=off")]
    [InlineData("  focus-border   =   off  ")]
    [InlineData("FOCUS-BORDER = OFF")]
    [InlineData("focus-border = false")]
    [InlineData("focus-border = 0")]
    public void TheBorderIsTurnedOff_HoweverTheLineIsSpelled(string line)
    {
        Assert.False(Settings.Parse(line).FocusBorder);
    }

    [Theory]
    [InlineData("focus-border = on")]
    [InlineData("focus-border = true")]
    [InlineData("focus-border = 1")]
    public void TheBorderIsTurnedOn_HoweverTheLineIsSpelled(string line)
    {
        Assert.True(Settings.Parse(line).FocusBorder);
    }

    /// <summary>
    /// A value nobody recognises is not a reason to change what the user is looking at. It keeps the
    /// default, exactly as an unparseable exception-list line is skipped rather than thrown on.
    /// </summary>
    [Theory]
    [InlineData("focus-border = perhaps")]
    [InlineData("focus-border =")]
    [InlineData("focus-border")]
    public void AnUnreadableValue_KeepsTheDefaultRatherThanGuessing(string line)
    {
        Assert.True(Settings.Parse(line).FocusBorder);
    }

    /// <summary>
    /// <c>gap</c> USED to be the unknown key this fact exercised, before T5 (alert-tile-mosaic) gave
    /// it a real meaning -- rewritten to a key nothing will ever recognise, so this still proves
    /// what it always proved (an unknown key costs nothing, the known one beside it still lands)
    /// instead of quietly becoming a second <c>gap</c> test.
    /// </summary>
    [Fact]
    public void CommentsBlankLinesAndUnknownKeys_AreIgnored()
    {
        var settings = Settings.Parse(
            """
            # CosmicWin settings

            not-a-real-setting = 12
            focus-border = off
            """);

        Assert.False(settings.FocusBorder);
    }

    /// <summary>The last word wins, so a file appended to twice reads as its most recent line.</summary>
    [Fact]
    public void TheLastAssignmentWins()
    {
        Assert.True(Settings.Parse("focus-border = off\nfocus-border = on").FocusBorder);
    }

    [Fact]
    public void CarriageReturnsAreNotPartOfTheValue()
    {
        Assert.False(Settings.Parse("focus-border = off\r\n").FocusBorder);
    }

    /// <summary>
    /// What is written must read back as itself. This is the fact that catches a serializer and a
    /// parser drifting apart -- the failure mode where saving a toggle silently resets it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SerializeThenParse_RoundTrips(bool focusBorder)
    {
        var original = new Settings(focusBorder);

        Assert.Equal(original, Settings.Parse(original.Serialize()));
    }

    /// <summary>The written file explains itself, because a human is expected to open it.</summary>
    [Fact]
    public void TheSerializedFileCarriesItsOwnComment()
    {
        Assert.Contains("#", new Settings(false).Serialize(), StringComparison.Ordinal);
    }

    /// <summary>
    /// No colour is a real answer, not a missing one: it means "whatever Windows' accent is right
    /// now", which is what the border drew before it could be configured at all.
    /// </summary>
    [Fact]
    public void NoBorderColour_MeansTheSystemAccent()
    {
        Assert.Null(Settings.Default.BorderColor);
        Assert.Null(Settings.Parse(string.Empty).BorderColor);
    }

    [Theory]
    [InlineData("border-color = #FF8800")]
    [InlineData("border-color=#ff8800")]
    [InlineData("  BORDER-COLOR  =  #Ff8800  ")]
    [InlineData("border-color = FF8800")]
    public void AColourIsRead_HoweverTheLineIsSpelled(string line)
    {
        Assert.Equal(0xFF8800u, Settings.Parse(line).BorderColor);
    }

    /// <summary>
    /// The three-digit form is what a person types from memory, and CSS taught them it works.
    /// Each digit doubles, so <c>#f80</c> is the same colour as <c>#ff8800</c> rather than a
    /// near-miss nobody can see is wrong.
    /// </summary>
    [Fact]
    public void TheShortHexForm_ExpandsTheWayCssDoes()
    {
        Assert.Equal(0xFF8800u, Settings.Parse("border-color = #f80").BorderColor);
    }

    /// <summary>The word that says "give it back to Windows", so the tray has a way home.</summary>
    [Fact]
    public void TheWordAccent_ClearsTheColour()
    {
        Assert.Null(Settings.Parse("border-color = #FF8800\nborder-color = accent").BorderColor);
    }

    /// <summary>
    /// Same rule the flag follows: only a value we RECOGNISE moves the setting. Guessing what
    /// "blue-ish" meant is worse than leaving the accent alone.
    /// </summary>
    [Theory]
    [InlineData("border-color = azul")]
    [InlineData("border-color = #12345")]
    [InlineData("border-color = #GGGGGG")]
    [InlineData("border-color =")]
    public void AnUnreadableColour_LeavesTheDefaultAlone(string line)
    {
        Assert.Null(Settings.Parse(line).BorderColor);
    }

    /// <summary>A bad colour costs the colour, never the flag that shares the file.</summary>
    [Fact]
    public void AnUnreadableColour_DoesNotCostTheOtherSetting()
    {
        Assert.False(Settings.Parse("focus-border = off\nborder-color = azul").FocusBorder);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0x000000u)]
    [InlineData(0xFF8800u)]
    [InlineData(0xFFFFFFu)]
    public void SerializeThenParse_RoundTripsTheColour(uint? colour)
    {
        var original = new Settings(FocusBorder: true, BorderColor: colour);

        Assert.Equal(original, Settings.Parse(original.Serialize()));
    }

    /// <summary>
    /// Tiling is ON unless the file says otherwise, for the same reason the border is: a file that
    /// has never been written must not turn the window manager's whole purpose off.
    /// </summary>
    [Fact]
    public void TilingIsOn_UnlessTheFileSaysOtherwise()
    {
        Assert.True(Settings.Default.Tiling);
        Assert.True(Settings.Parse(string.Empty).Tiling);
    }

    [Theory]
    [InlineData("tiling = off")]
    [InlineData("tiling=off")]
    [InlineData("  TILING   =   Off  ")]
    [InlineData("tiling = false")]
    [InlineData("tiling = 0")]
    public void TilingIsTurnedOff_HoweverTheLineIsSpelled(string line)
    {
        Assert.False(Settings.Parse(line).Tiling);
    }

    /// <summary>Same rule as every other key: a value nobody recognises keeps the default.</summary>
    [Theory]
    [InlineData("tiling = quizas")]
    [InlineData("tiling =")]
    [InlineData("tiling")]
    public void AnUnreadableTilingValue_KeepsTheDefaultRatherThanGuessing(string line)
    {
        Assert.True(Settings.Parse(line).Tiling);
    }

    /// <summary>Each setting costs only itself: an unreadable one must not take its neighbours down.</summary>
    [Fact]
    public void TilingIsReadIndependentlyOfTheOtherSettings()
    {
        var settings = Settings.Parse("focus-border = off\ntiling = off\nborder-color = azul");

        Assert.False(settings.FocusBorder);
        Assert.False(settings.Tiling);
        Assert.Null(settings.BorderColor);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SerializeThenParse_RoundTripsTheTilingSwitch(bool tiling)
    {
        var original = new Settings(FocusBorder: true, BorderColor: null, Tiling: tiling);

        Assert.Equal(original, Settings.Parse(original.Serialize()));
    }

    /// <summary>
    /// No video configured is the correct default, for the same reason no border colour is: a
    /// settings file that has never been written must not start playing a video nobody chose.
    /// </summary>
    [Fact]
    public void NoVideoWallpaperPath_IsTheDefault()
    {
        Assert.Null(Settings.Default.VideoWallpaperPath);
        Assert.Null(Settings.Parse(string.Empty).VideoWallpaperPath);
    }

    [Theory]
    [InlineData("video-wallpaper-path = C:\\some\\path.mp4")]
    [InlineData("video-wallpaper-path=C:\\some\\path.mp4")]
    [InlineData("  VIDEO-WALLPAPER-PATH   =   C:\\some\\path.mp4  ")]
    public void AVideoWallpaperPathIsRead_HoweverTheLineIsSpelled(string line)
    {
        Assert.Equal("C:\\some\\path.mp4", Settings.Parse(line).VideoWallpaperPath);
    }

    /// <summary>
    /// Same rule as every other key: a blank value is not a path. Reading it as an empty string
    /// would turn "nothing configured" into a path nothing can open.
    /// </summary>
    [Theory]
    [InlineData("video-wallpaper-path =")]
    [InlineData("video-wallpaper-path")]
    public void ABlankVideoWallpaperPath_LeavesTheDefaultAlone(string line)
    {
        Assert.Null(Settings.Parse(line).VideoWallpaperPath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("C:\\some\\path.mp4")]
    public void SerializeThenParse_RoundTripsTheVideoWallpaperPath(string? videoWallpaperPath)
    {
        var original = new Settings(FocusBorder: true, BorderColor: null, Tiling: true,
            VideoWallpaperPath: videoWallpaperPath);

        Assert.Equal(original, Settings.Parse(original.Serialize()));
    }

    [Theory]
    [InlineData("alerts = off")]
    [InlineData("alerts-enabled=off")]
    [InlineData("  ALERTS-ENABLED   =   Off  ")]
    [InlineData("alerts-enabled = false")]
    [InlineData("alerts-enabled = 0")]
    public void AlertsAreTurnedOff_HoweverTheLineIsSpelled(string line)
    {
        Assert.False(Settings.Parse(line).AlertsEnabled);
    }

    [Theory]
    [InlineData("alerts-enabled = on")]
    [InlineData("alerts-enabled = true")]
    [InlineData("alerts-enabled = 1")]
    public void AlertsAreTurnedOn_HoweverTheLineIsSpelled(string line)
    {
        Assert.True(Settings.Parse(line).AlertsEnabled);
    }

    [Theory]
    [InlineData("alerts-enabled = perhaps")]
    [InlineData("alerts-enabled =")]
    [InlineData("alerts-enabled")]
    public void AnUnreadableAlertsValue_KeepsTheDefaultRatherThanGuessing(string line)
    {
        Assert.True(Settings.Parse(line).AlertsEnabled);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SerializeThenParse_RoundTripsTheAlertsSwitch(bool alertsEnabled)
    {
        var original = new Settings(FocusBorder: true, BorderColor: null, Tiling: true,
            VideoWallpaperPath: null, AlertsEnabled: alertsEnabled);

        Assert.Equal(original, Settings.Parse(original.Serialize()));
    }

    [Fact]
    public void Serialize_IncludesTheAlertsSwitchAndComment()
    {
        var serialized = new Settings(FocusBorder: true, AlertsEnabled: false).Serialize();

        Assert.Contains("# alerts:", serialized, StringComparison.Ordinal);
        Assert.Contains("alerts = off", serialized, StringComparison.Ordinal);
    }

    /// <summary>
    /// ON unless the file says otherwise, since S9 (wallpaper-scene-http-endpoint, 2026-09-27,
    /// maintainer's decision: "alertas tambien debe estar prendidas") -- loopback-only and gated by a
    /// bearer token, so a fresh install opens this port as an accepted consequence, the same call S8
    /// made for <see cref="Settings.WallpaperSceneHttpEnabled"/>.
    /// </summary>
    [Fact]
    public void AlertHttpIsOn_UnlessTheFileSaysOtherwise()
    {
        Assert.True(Settings.Default.HttpServerEnabled);
        Assert.True(Settings.Parse(string.Empty).HttpServerEnabled);
    }

    [Theory]
    [InlineData("http-server = on")]
    [InlineData("alert-http=on")]
    [InlineData("  ALERT-HTTP   =   On  ")]
    [InlineData("alert-http = true")]
    [InlineData("alert-http = 1")]
    public void AlertHttpIsTurnedOn_HoweverTheLineIsSpelled(string line)
    {
        Assert.True(Settings.Parse(line).HttpServerEnabled);
    }

    [Theory]
    [InlineData("alert-http = off")]
    [InlineData("alert-http = false")]
    [InlineData("alert-http = 0")]
    public void AlertHttpIsTurnedOff_HoweverTheLineIsSpelled(string line)
    {
        Assert.False(Settings.Parse(line).HttpServerEnabled);
    }

    [Theory]
    [InlineData("alert-http = perhaps")]
    [InlineData("alert-http =")]
    [InlineData("alert-http")]
    public void AnUnreadableAlertHttpValue_KeepsTheDefaultRatherThanGuessing(string line)
    {
        Assert.True(Settings.Parse(line).HttpServerEnabled);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SerializeThenParse_RoundTripsTheAlertHttpSwitch(bool alertHttpEnabled)
    {
        var original = new Settings(FocusBorder: true, HttpServerEnabled: alertHttpEnabled);

        Assert.Equal(original, Settings.Parse(original.Serialize()));
    }

    /// <summary>
    /// <see cref="CosmicWin.Interop.AlertHttpProtocol.DefaultPort"/> unless the file says otherwise --
    /// the exact port the endpoint would actually bind to, so a default settings file and a default
    /// server agree on where the endpoint lives.
    /// </summary>
    [Fact]
    public void AlertHttpPortDefaultsToTheProtocolConstant()
    {
        Assert.Equal(CosmicWin.Interop.AlertHttpProtocol.DefaultPort, Settings.Default.HttpServerPort);
        Assert.Equal(CosmicWin.Interop.AlertHttpProtocol.DefaultPort, Settings.Parse(string.Empty).HttpServerPort);
    }

    [Theory]
    [InlineData("http-server-port = 8080")]
    [InlineData("alert-http-port=8080")]
    [InlineData("  ALERT-HTTP-PORT   =   8080  ")]
    [InlineData("alert-http-port = 1")]
    [InlineData("alert-http-port = 65535")]
    public void AlertHttpPortIsRead_HoweverTheLineIsSpelled(string line)
    {
        var expected = int.Parse(line.Split('=')[1].Trim());

        Assert.Equal(expected, Settings.Parse(line).HttpServerPort);
    }

    /// <summary>
    /// Same rule as every other key: a port outside 1-65535, or not a whole number at all, keeps the
    /// default rather than opening a port that does not make sense.
    /// </summary>
    [Theory]
    [InlineData("alert-http-port = 0")]
    [InlineData("alert-http-port = 70000")]
    [InlineData("alert-http-port = -1")]
    [InlineData("alert-http-port = abc")]
    [InlineData("alert-http-port =")]
    [InlineData("alert-http-port")]
    public void AnInvalidAlertHttpPort_KeepsTheDefaultRatherThanGuessing(string line)
    {
        Assert.Equal(CosmicWin.Interop.AlertHttpProtocol.DefaultPort, Settings.Parse(line).HttpServerPort);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(47811)]
    [InlineData(65535)]
    public void SerializeThenParse_RoundTripsTheAlertHttpPort(int port)
    {
        var original = new Settings(FocusBorder: true, HttpServerPort: port);

        Assert.Equal(original, Settings.Parse(original.Serialize()));
    }

    [Fact]
    public void Serialize_IncludesTheAlertHttpSwitchAndPortWithComments()
    {
        var serialized = new Settings(FocusBorder: true, HttpServerEnabled: true, HttpServerPort: 8080).Serialize();

        Assert.Contains("# http-server:", serialized, StringComparison.Ordinal);
        Assert.Contains("http-server = on", serialized, StringComparison.Ordinal);
        Assert.Contains("# http-server-port:", serialized, StringComparison.Ordinal);
        Assert.Contains("http-server-port = 8080", serialized, StringComparison.Ordinal);
    }

    /// <summary>The comment tells the user where the token lives and that the endpoint never leaves the machine.</summary>
    [Fact]
    public void TheAlertHttpComment_NamesTheTokenFileAndLoopbackOnly()
    {
        var serialized = new Settings(FocusBorder: true).Serialize();

        Assert.Contains("alert-http.token", serialized, StringComparison.Ordinal);
        Assert.Contains("loopback", serialized, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Each setting costs only itself: an unreadable one must not take its neighbours down.</summary>
    [Fact]
    public void AlertHttpSettingsAreReadIndependentlyOfTheOtherSettings()
    {
        var settings = Settings.Parse("focus-border = off\nalert-http = on\nalert-http-port = 9999");

        Assert.False(settings.FocusBorder);
        Assert.True(settings.HttpServerEnabled);
        Assert.Equal(9999, settings.HttpServerPort);
    }


    /// <summary>
    /// T5 (alert-tile-mosaic, maintainer decision 2026-09-26): <see cref="TreeArranger.DefaultGap"/>
    /// unless the file says otherwise, the exact value <c>TreeArranger.Gap</c> was hard-set to before
    /// this key existed -- a settings file that has never been written must draw the same gap it
    /// always did.
    /// </summary>
    [Fact]
    public void GapDefaultsToTheTilingEnginesOwnDefault()
    {
        Assert.Equal(TreeArranger.DefaultGap, Settings.Default.Gap);
        Assert.Equal(TreeArranger.DefaultGap, Settings.Parse(string.Empty).Gap);
    }

    [Theory]
    [InlineData("gap = 0")]
    [InlineData("gap=0")]
    [InlineData("  GAP   =   0  ")]
    [InlineData("gap = 24")]
    [InlineData("gap = 64")]
    public void GapIsRead_HoweverTheLineIsSpelled(string line)
    {
        var expected = int.Parse(line.Split('=')[1].Trim());

        Assert.Equal(expected, Settings.Parse(line).Gap);
    }

    /// <summary>
    /// Same rule as every other key: a value outside 0-64, or not a whole number at all, keeps the
    /// default rather than drawing a gap nobody asked for.
    /// </summary>
    [Theory]
    [InlineData("gap = -1")]
    [InlineData("gap = 65")]
    [InlineData("gap = abc")]
    [InlineData("gap = 12.5")]
    [InlineData("gap =")]
    [InlineData("gap")]
    public void AnInvalidGap_KeepsTheDefaultRatherThanGuessing(string line)
    {
        Assert.Equal(TreeArranger.DefaultGap, Settings.Parse(line).Gap);
    }

    /// <summary>The last word wins here too, the same rule <see cref="TheLastAssignmentWins"/> proves for the border.</summary>
    [Fact]
    public void TheLastGapAssignmentWins()
    {
        Assert.Equal(24, Settings.Parse("gap = 8\ngap = 24").Gap);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(24)]
    [InlineData(64)]
    public void SerializeThenParse_RoundTripsTheGap(int gap)
    {
        var original = new Settings(FocusBorder: true, Gap: gap);

        Assert.Equal(original, Settings.Parse(original.Serialize()));
    }

    [Fact]
    public void Serialize_IncludesTheGapAndComment()
    {
        var serialized = new Settings(FocusBorder: true, Gap: 24).Serialize();

        Assert.Contains("# gap:", serialized, StringComparison.Ordinal);
        Assert.Contains("gap = 24", serialized, StringComparison.Ordinal);
    }

    /// <summary>Each setting costs only itself: an unreadable one must not take its neighbours down.</summary>
    [Fact]
    public void GapIsReadIndependentlyOfTheOtherSettings()
    {
        var settings = Settings.Parse("focus-border = off\ngap = 65\ntiling = off");

        Assert.False(settings.FocusBorder);
        Assert.Equal(TreeArranger.DefaultGap, settings.Gap);
        Assert.False(settings.Tiling);
    }

    /// <summary>
    /// S8 (wallpaper-scene-http-endpoint, 2026-09-27): <see cref="WallpaperMode.Html"/> unless the
    /// file says otherwise -- the html wallpaper (built as D3's demo, html-wallpaper-demo) graduated
    /// to CosmicWin's default renderer. <see cref="WallpaperMode.Video"/> remains fully supported for
    /// anyone who sets `wallpaper-mode = video`.
    /// </summary>
    [Fact]
    public void WallpaperModeDefaultsToHtml()
    {
        Assert.Equal(WallpaperMode.Html, Settings.Default.WallpaperMode);
        Assert.Equal(WallpaperMode.Html, Settings.Parse(string.Empty).WallpaperMode);
    }

    [Theory]
    [InlineData("wallpaper-mode = html")]
    [InlineData("wallpaper-mode=html")]
    [InlineData("  WALLPAPER-MODE   =   Html  ")]
    public void WallpaperModeIsReadAsHtml_HoweverTheLineIsSpelled(string line)
    {
        Assert.Equal(WallpaperMode.Html, Settings.Parse(line).WallpaperMode);
    }

    [Theory]
    [InlineData("wallpaper-mode = video")]
    [InlineData("wallpaper-mode=video")]
    [InlineData("  WALLPAPER-MODE   =   Video  ")]
    public void WallpaperModeIsReadAsVideo_HoweverTheLineIsSpelled(string line)
    {
        Assert.Equal(WallpaperMode.Video, Settings.Parse(line).WallpaperMode);
    }

    /// <summary>Same rule as every other key: a value nobody recognises keeps the default rather than guessing.</summary>
    [Theory]
    [InlineData("wallpaper-mode = perhaps")]
    [InlineData("wallpaper-mode =")]
    [InlineData("wallpaper-mode")]
    public void AnUnreadableWallpaperMode_KeepsTheDefaultRatherThanGuessing(string line)
    {
        Assert.Equal(WallpaperMode.Html, Settings.Parse(line).WallpaperMode);
    }

    [Theory]
    [InlineData(WallpaperMode.Video)]
    [InlineData(WallpaperMode.Html)]
    public void SerializeThenParse_RoundTripsTheWallpaperMode(WallpaperMode wallpaperMode)
    {
        var original = new Settings(FocusBorder: true, WallpaperMode: wallpaperMode);

        Assert.Equal(original, Settings.Parse(original.Serialize()));
    }

    /// <summary>The written file's comment names the default and describes the alternative, not a demo disclaimer.</summary>
    [Fact]
    public void Serialize_IncludesTheWallpaperModeAndDescribesEachValue()
    {
        var serialized = new Settings(FocusBorder: true, WallpaperMode: WallpaperMode.Html).Serialize();

        Assert.Contains("# wallpaper-mode:", serialized, StringComparison.Ordinal);
        Assert.Contains("html` (default)", serialized, StringComparison.Ordinal);
        Assert.Contains("wallpaper-mode = html", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("DEMO ONLY", serialized, StringComparison.Ordinal);
    }

    /// <summary>Each setting costs only itself: an unreadable one must not take its neighbours down.</summary>
    [Fact]
    public void WallpaperModeIsReadIndependentlyOfTheOtherSettings()
    {
        var settings = Settings.Parse("focus-border = off\nwallpaper-mode = html\ntiling = off");

        Assert.False(settings.FocusBorder);
        Assert.Equal(WallpaperMode.Html, settings.WallpaperMode);
        Assert.False(settings.Tiling);
    }

    /// <summary>
    /// D6d (html-wallpaper-demo, 2026-09-27): <see cref="WallpaperScene.Processing"/> unless the file
    /// says otherwise -- a settings file that has never been written must not switch the html
    /// wallpaper away from the one scene that shipped before D6d.
    /// </summary>
    [Fact]
    public void WallpaperSceneDefaultsToProcessing()
    {
        Assert.Equal(WallpaperScene.Processing, Settings.Default.WallpaperScene);
        Assert.Equal(WallpaperScene.Processing, Settings.Parse(string.Empty).WallpaperScene);
    }

    [Theory]
    [InlineData("wallpaper-scene = explorer", WallpaperScene.Explorer)]
    [InlineData("wallpaper-scene=explorer", WallpaperScene.Explorer)]
    [InlineData("  WALLPAPER-SCENE   =   Explorer  ", WallpaperScene.Explorer)]
    [InlineData("wallpaper-scene = idle", WallpaperScene.Idle)]
    [InlineData("wallpaper-scene = IDLE", WallpaperScene.Idle)]
    [InlineData("wallpaper-scene = raphael", WallpaperScene.Raphael)]
    [InlineData("wallpaper-scene = Raphael", WallpaperScene.Raphael)]
    [InlineData("wallpaper-scene = processing", WallpaperScene.Processing)]
    [InlineData("wallpaper-scene = PROCESSING", WallpaperScene.Processing)]
    public void WallpaperSceneIsReadAsEachOfTheFourFixedValues_HoweverTheLineIsSpelled(
        string line, WallpaperScene expected)
    {
        Assert.Equal(expected, Settings.Parse(line).WallpaperScene);
    }

    /// <summary>Same rule as every other key: a value nobody recognises keeps the default rather than guessing.</summary>
    [Theory]
    [InlineData("wallpaper-scene = perhaps")]
    [InlineData("wallpaper-scene =")]
    [InlineData("wallpaper-scene")]
    public void AnUnreadableWallpaperScene_KeepsTheDefaultRatherThanGuessing(string line)
    {
        Assert.Equal(WallpaperScene.Processing, Settings.Parse(line).WallpaperScene);
    }

    [Theory]
    [InlineData(WallpaperScene.Processing)]
    [InlineData(WallpaperScene.Explorer)]
    [InlineData(WallpaperScene.Idle)]
    [InlineData(WallpaperScene.Raphael)]
    public void SerializeThenParse_RoundTripsTheWallpaperScene(WallpaperScene wallpaperScene)
    {
        var original = new Settings(FocusBorder: true, WallpaperScene: wallpaperScene);

        Assert.Equal(original, Settings.Parse(original.Serialize()));
    }

    /// <summary>The written file's comment names the default scene and lists the other three, not a demo disclaimer.</summary>
    [Fact]
    public void Serialize_IncludesTheWallpaperSceneAndDescribesEachValue()
    {
        var serialized = new Settings(FocusBorder: true, WallpaperScene: WallpaperScene.Raphael).Serialize();

        Assert.Contains("# wallpaper-scene:", serialized, StringComparison.Ordinal);
        Assert.Contains("processing` (default)", serialized, StringComparison.Ordinal);
        Assert.Contains("wallpaper-scene = raphael", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("DEMO ONLY", serialized, StringComparison.Ordinal);
    }

    /// <summary>
    /// D6d: <c>60</c> unless the file says otherwise -- a settings file that has never been written
    /// must not cap the html wallpaper's frame rate below its own historical, uncapped behaviour.
    /// </summary>
    [Fact]
    public void WallpaperFpsDefaultsTo60()
    {
        Assert.Equal(60, Settings.Default.WallpaperFps);
        Assert.Equal(60, Settings.Parse(string.Empty).WallpaperFps);
    }

    [Theory]
    [InlineData("wallpaper-fps = 30", 30)]
    [InlineData("wallpaper-fps=30", 30)]
    [InlineData("  WALLPAPER-FPS   =   30  ", 30)]
    [InlineData("wallpaper-fps = 60", 60)]
    [InlineData("wallpaper-fps=60", 60)]
    public void WallpaperFpsIsReadAsEachOfTheTwoFixedValues_HoweverTheLineIsSpelled(string line, int expected)
    {
        Assert.Equal(expected, Settings.Parse(line).WallpaperFps);
    }

    /// <summary>Same rule as every other key: a value nobody recognises -- including any OTHER number -- keeps the default.</summary>
    [Theory]
    [InlineData("wallpaper-fps = 45")]
    [InlineData("wallpaper-fps = 0")]
    [InlineData("wallpaper-fps = -30")]
    [InlineData("wallpaper-fps = perhaps")]
    [InlineData("wallpaper-fps =")]
    [InlineData("wallpaper-fps")]
    public void AnUnreadableWallpaperFps_KeepsTheDefaultRatherThanGuessing(string line)
    {
        Assert.Equal(60, Settings.Parse(line).WallpaperFps);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public void SerializeThenParse_RoundTripsTheWallpaperFps(int wallpaperFps)
    {
        var original = new Settings(FocusBorder: true, WallpaperFps: wallpaperFps);

        Assert.Equal(original, Settings.Parse(original.Serialize()));
    }

    /// <summary>The written file's comment names the default frame rate and the other fixed value, not a demo disclaimer.</summary>
    [Fact]
    public void Serialize_IncludesTheWallpaperFpsAndDescribesEachValue()
    {
        var serialized = new Settings(FocusBorder: true, WallpaperFps: 30).Serialize();

        Assert.Contains("# wallpaper-fps:", serialized, StringComparison.Ordinal);
        Assert.Contains("60` (default)", serialized, StringComparison.Ordinal);
        Assert.Contains("wallpaper-fps = 30", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("DEMO ONLY", serialized, StringComparison.Ordinal);
    }

    /// <summary>Each setting costs only itself: an unreadable one must not take its neighbours down.</summary>
    [Fact]
    public void WallpaperSceneAndFpsAreReadIndependentlyOfTheOtherSettings()
    {
        var settings = Settings.Parse(
            "focus-border = off\nwallpaper-scene = idle\nwallpaper-fps = 30\ntiling = off");

        Assert.False(settings.FocusBorder);
        Assert.Equal(WallpaperScene.Idle, settings.WallpaperScene);
        Assert.Equal(30, settings.WallpaperFps);
        Assert.False(settings.Tiling);
    }


    [Theory]
    [InlineData("wallpaper-mode = html-mini")]
    [InlineData("wallpaper-mode=mini")]
    [InlineData("  WALLPAPER-MODE   =   Mini  ")]
    public void WallpaperModeIsReadAsMini_HoweverTheLineIsSpelled(string line)
    {
        Assert.Equal(WallpaperMode.HtmlMini, Settings.Parse(line).WallpaperMode);
    }

    [Fact]
    public void SerializeThenParse_RoundTripsTheMiniWallpaperMode()
    {
        var original = new Settings(FocusBorder: true, WallpaperMode: WallpaperMode.HtmlMini);

        Assert.Equal(original, Settings.Parse(original.Serialize()));
        Assert.Contains("wallpaper-mode = html-mini", original.Serialize(), StringComparison.Ordinal);
    }

    [Fact]
    public void MiniCornerDefaultsToTopRight()
    {
        Assert.Equal(MiniPosition.TopRight, Settings.Default.MiniPosition);
        Assert.Equal(MiniPosition.TopRight, Settings.Parse(string.Empty).MiniPosition);
    }

    [Theory]
    [InlineData("mini-corner = top-left", MiniPosition.TopLeft)]
    [InlineData("mini-corner=top-right", MiniPosition.TopRight)]
    [InlineData("mini-corner = bottom-left", MiniPosition.BottomLeft)]
    [InlineData("  MINI-CORNER  =  Bottom-Right ", MiniPosition.BottomRight)]
    [InlineData("mini-corner = top-center", MiniPosition.TopCenter)]
    [InlineData("mini-corner = right-center", MiniPosition.RightCenter)]
    [InlineData("mini-corner = bottom-center", MiniPosition.BottomCenter)]
    [InlineData("MINI-CORNER = Left-Center", MiniPosition.LeftCenter)]
    public void MiniCornerIsRead_HoweverTheLineIsSpelled(string line, MiniPosition expected)
    {
        Assert.Equal(expected, Settings.Parse(line).MiniPosition);
    }

    [Theory]
    [InlineData("mini-corner = middle")]
    [InlineData("mini-corner =")]
    [InlineData("mini-corner")]
    public void AnUnreadableMiniCorner_KeepsTheDefaultRatherThanGuessing(string line)
    {
        Assert.Equal(MiniPosition.TopRight, Settings.Parse(line).MiniPosition);
    }

    [Theory]
    [InlineData(MiniPosition.TopLeft)]
    [InlineData(MiniPosition.TopRight)]
    [InlineData(MiniPosition.BottomLeft)]
    [InlineData(MiniPosition.BottomRight)]
    [InlineData(MiniPosition.TopCenter)]
    [InlineData(MiniPosition.RightCenter)]
    [InlineData(MiniPosition.BottomCenter)]
    [InlineData(MiniPosition.LeftCenter)]
    public void SerializeThenParse_RoundTripsTheMiniCorner(MiniPosition corner)
    {
        var original = new Settings(FocusBorder: true, MiniPosition: corner);

        Assert.Equal(original, Settings.Parse(original.Serialize()));
    }

    [Fact]
    public void Serialize_IncludesTheMiniCornerAndListsEachValue()
    {
        var serialized = Settings.Default.Serialize();

        Assert.Contains("# mini-position:", serialized, StringComparison.Ordinal);
        Assert.Contains("mini-position = top-right", serialized, StringComparison.Ordinal);
        Assert.Contains("top-left", serialized, StringComparison.Ordinal);
        Assert.Contains("bottom-right", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void MiniCornerIsReadIndependentlyOfTheOtherSettings()
    {
        var settings = Settings.Parse("focus-border = off\nmini-corner = top-left\ntiling = off");

        Assert.False(settings.FocusBorder);
        Assert.Equal(MiniPosition.TopLeft, settings.MiniPosition);
        Assert.False(settings.Tiling);
    }

    // ---- T10: normalized keys, with migration of the legacy ones ----

    [Fact]
    public void Defaults_AreUnchangedByTheKeyNormalization()
    {
        var d = Settings.Default;

        Assert.True(d.FocusBorder);
        Assert.True(d.Tiling);
        Assert.True(d.AlertsEnabled);
        Assert.True(d.HttpServerEnabled);
        Assert.Equal(AlertHttpProtocol.DefaultPort, d.HttpServerPort);
        Assert.Equal(WallpaperMode.Html, d.WallpaperMode);
        Assert.Equal(WallpaperScene.Processing, d.WallpaperScene);
        Assert.Equal(60, d.WallpaperFps);
        Assert.Equal(MiniPosition.TopRight, d.MiniPosition);
        Assert.Equal(d, Settings.Parse(string.Empty));
    }

    [Fact]
    public void EachNewKey_IsParsed()
    {
        var settings = Settings.Parse(
            "alerts = off\nhttp-server = off\nhttp-server-port = 5555\n"
            + "wallpaper-mode = html-mini\nmini-position = bottom-left");

        Assert.False(settings.AlertsEnabled);
        Assert.False(settings.HttpServerEnabled);
        Assert.Equal(5555, settings.HttpServerPort);
        Assert.Equal(WallpaperMode.HtmlMini, settings.WallpaperMode);
        Assert.Equal(MiniPosition.BottomLeft, settings.MiniPosition);
    }

    [Fact]
    public void EachLegacyAlias_IsStillParsed()
    {
        var settings = Settings.Parse(
            "alerts-enabled = off\nalert-http = off\nalert-http-port = 5556\n"
            + "wallpaper-mode = mini\nmini-corner = left-center");

        Assert.False(settings.AlertsEnabled);
        Assert.False(settings.HttpServerEnabled);
        Assert.Equal(5556, settings.HttpServerPort);
        Assert.Equal(WallpaperMode.HtmlMini, settings.WallpaperMode);
        Assert.Equal(MiniPosition.LeftCenter, settings.MiniPosition);
    }

    [Theory]
    [InlineData("alerts = off\nalerts-enabled = on")]
    [InlineData("alerts-enabled = on\nalerts = off")]
    public void NewAlertsKeyWins_InEitherLineOrder(string content)
    {
        Assert.False(Settings.Parse(content).AlertsEnabled);
    }

    [Theory]
    [InlineData("http-server = off\nalert-http = on")]
    [InlineData("alert-http = on\nhttp-server = off")]
    public void NewHttpServerKeyWins_InEitherLineOrder(string content)
    {
        Assert.False(Settings.Parse(content).HttpServerEnabled);
    }

    [Theory]
    [InlineData("http-server-port = 6001\nalert-http-port = 7001")]
    [InlineData("alert-http-port = 7001\nhttp-server-port = 6001")]
    public void NewHttpServerPortKeyWins_InEitherLineOrder(string content)
    {
        Assert.Equal(6001, Settings.Parse(content).HttpServerPort);
    }

    [Theory]
    [InlineData("mini-position = top-left\nmini-corner = bottom-right")]
    [InlineData("mini-corner = bottom-right\nmini-position = top-left")]
    public void NewMiniPositionKeyWins_InEitherLineOrder(string content)
    {
        Assert.Equal(MiniPosition.TopLeft, Settings.Parse(content).MiniPosition);
    }

    [Fact]
    public void AnUnreadableNewKey_DoesNotShadowAReadableLegacyOne()
    {
        var settings = Settings.Parse("http-server = perhaps\nalert-http = off");

        Assert.False(settings.HttpServerEnabled);
    }

    [Theory]
    [InlineData("video-wallpaper-http = off")]
    [InlineData("wallpaper-scene-http = off")]
    [InlineData("video-wallpaper-http = on\nwallpaper-scene-http = on")]
    public void RemovedPerRouteKeys_AreAcceptedAndIgnored(string content)
    {
        Assert.Equal(Settings.Default, Settings.Parse(content));
    }

    [Fact]
    public void Serialize_WritesOnlyTheNewKeys()
    {
        var text = Settings.Default.Serialize();
        var keys = text.Split('\n')
            .Where(line => !line.TrimStart().StartsWith('#') && line.Contains('='))
            .Select(line => line[..line.IndexOf('=')].Trim())
            .ToArray();

        Assert.Equal(
            ["focus-border", "border-color", "tiling", "video-wallpaper-path", "alerts", "http-server",
             "http-server-port", "gap", "wallpaper-mode", "wallpaper-scene", "wallpaper-fps", "mini-position"],
            keys);
        foreach (var legacy in new[] { "alerts-enabled", "alert-http =", "alert-http-port", "video-wallpaper-http",
                     "wallpaper-scene-http", "mini-corner" })
        {
            Assert.DoesNotContain(legacy, text, StringComparison.Ordinal);
        }
    }

    /// <summary>The maintainer's real-world legacy file shape survives a save unchanged in meaning.</summary>
    [Fact]
    public void LegacyRealWorldFile_RoundTripsThroughSerializeToTheSameEffectiveSettings()
    {
        var legacy = Settings.Parse(
            "alerts-enabled=on\nalert-http=on\nalert-http-port=43811\nvideo-wallpaper-http=on\n"
            + "wallpaper-mode=mini\nwallpaper-scene=idle\nwallpaper-fps=60\nwallpaper-scene-http=on\n"
            + "mini-corner=top-right");

        var reparsed = Settings.Parse(legacy.Serialize());

        Assert.Equal(legacy, reparsed);
        Assert.Equal(43811, reparsed.HttpServerPort);
        Assert.Equal(WallpaperMode.HtmlMini, reparsed.WallpaperMode);
        Assert.Equal(WallpaperScene.Idle, reparsed.WallpaperScene);
        Assert.Equal(MiniPosition.TopRight, reparsed.MiniPosition);
        Assert.True(reparsed.AlertsEnabled);
        Assert.True(reparsed.HttpServerEnabled);
        Assert.Equal(60, reparsed.WallpaperFps);
    }
}
