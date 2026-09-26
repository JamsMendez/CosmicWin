namespace CosmicWin.App.Tests.Alerts;

/// <summary>
/// T1 (webview-alert-layer): the trimmed, transparent, offline copy of the great-sage page's alert
/// layer -- <c>CosmicWin.App/Alerts/Web/alert-layer.html</c> (+ <c>.css</c>/<c>.js</c>) -- shipped as
/// loose content files next to the app's own executable, the shape <c>SetVirtualHostNameToFolderMapping</c>
/// (T3) needs to point a virtual host at a real folder.
/// </summary>
/// <remarks>
/// These are plain file/text assertions, not a browser: nothing here drives a DOM or a canvas, since
/// that needs an actual WebView2/browser host (T3, then a manual check -- see the class remarks
/// below). What CAN be proven without one: the files land where T3's folder mapping will look, they
/// carry no network dependency (source page loaded Archivo Black from Google Fonts; this trimmed copy
/// must not, per the feature doc's "Offline" constraint), the page exposes the documented hash-driven
/// API and the <c>done</c> handshake, and the CSS asks for a fully transparent surface -- a windowed
/// WebView2 composited over the video wallpaper (T2) shows nothing else through it.
/// </remarks>
public sealed class AlertLayerWebPageTests
{
    private static readonly string WebRoot =
        Path.Combine(AppContext.BaseDirectory, "Alerts", "Web");

    private static string ReadShipped(string relativePath) =>
        File.ReadAllText(Path.Combine(WebRoot, relativePath));

    [Theory]
    [InlineData("alert-layer.html")]
    [InlineData("alert-layer.css")]
    [InlineData("alert-layer.js")]
    [InlineData("fonts/ArchivoBlack-Regular.ttf")]
    [InlineData("fonts/OFL.txt")]
    public void ShippedFile_LandsInTheBuildOutputNextToTheExecutable(string relativePath)
    {
        var path = Path.Combine(WebRoot, relativePath);
        Assert.True(File.Exists(path), $"Expected '{path}' to be copied to the build output.");
    }

    /// <summary>
    /// Offline constraint: the page that loads Archivo Black from Google Fonts is the one thing this
    /// trimmed copy must not do (feature doc, "Offline"). Every SHIPPED source file is scanned except
    /// <c>OFL.txt</c> itself, which legitimately quotes an <c>http://</c> URL to the license text.
    /// </summary>
    [Theory]
    [InlineData("alert-layer.html")]
    [InlineData("alert-layer.css")]
    [InlineData("alert-layer.js")]
    public void ShippedSource_HasNoNetworkReferences(string relativePath)
    {
        var text = ReadShipped(relativePath);
        Assert.DoesNotContain("http://", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fonts.googleapis.com", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fonts.gstatic.com", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The documented API: <c>alert-layer.html#kind=failed&amp;duration=5000</c> starts the layer
    /// immediately, driven by <c>location.hash</c> (or a query string, for a plain-browser manual
    /// check), never a script the host has to inject.
    /// </summary>
    [Fact]
    public void Script_ReadsKindAndDurationFromTheHash()
    {
        var js = ReadShipped("alert-layer.js");
        Assert.Contains("location.hash", js);
        Assert.Contains("kind", js);
        Assert.Contains("duration", js);
    }

    /// <summary>
    /// The other half of the API: when the layer finishes, it tells its host by posting
    /// <c>'done'</c> through the WebView2 bridge -- guarded so the same file still runs (and can be
    /// manually checked) in a plain browser tab where <c>window.chrome.webview</c> does not exist.
    /// </summary>
    [Fact]
    public void Script_PostsDoneThroughTheWebViewBridgeWhenPresent()
    {
        var js = ReadShipped("alert-layer.js");
        Assert.Contains("window.chrome", js);
        Assert.Contains("webview", js);
        Assert.Contains("postMessage", js);
        Assert.Contains("\"done\"", js);
    }

    /// <summary>
    /// The page cannot see the video wallpaper behind it (T2) -- html, body and the one canvas must
    /// all stay fully transparent, with no page background painted over the video.
    /// </summary>
    [Fact]
    public void Css_KeepsHtmlBodyAndCanvasTransparent()
    {
        var css = ReadShipped("alert-layer.css");
        Assert.Contains("background: transparent", css);
        Assert.DoesNotContain("background-color:", css, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Structural fidelity, not string noise: the trimmed page keeps the source page's actual theme
    /// data (bit strings, titles, the failed reveal timing the task requires) rather
    /// than a paraphrase of it.
    /// </summary>
    [Fact]
    public void Script_CarriesTheSourcePagesThemeDataVerbatim()
    {
        var js = ReadShipped("alert-layer.js");
        Assert.Contains("0100010101010010010100100100111101010010", js); // FAILURE_OVERLAY_BITS
        Assert.Contains("01010111010000010101001001001110010010010100111001000111", js); // WARNING_OVERLAY_BITS
        Assert.Contains("\"FAILED\"", js);
        Assert.Contains("\"WARNING\"", js);
        Assert.Contains("var FAILURE_SHAKE_MS = 120;", js);
        Assert.Contains("var FAILURE_REVEAL_MS = 350;", js);
        Assert.Contains("revealMs: FAILURE_REVEAL_MS", js);
        Assert.Contains("revealMs: 700", js);
        Assert.Contains("shakeMs + theme.revealMs", js); // Failed reaches shown at 120 + 350 = 470 ms.
    }

    /// <summary>
    /// Only the alert-layer functions the feature doc names -- the nebula/scene, stars, orbits,
    /// keyboard shortcuts, fullscreen, zoom, self-check block and the canvas shake are all out of
    /// scope and must not have been carried over.
    /// </summary>
    [Fact]
    public void Script_DropsWhatIsOutOfScope()
    {
        var js = ReadShipped("alert-layer.js");
        // Named only in a comment explaining that it was dropped -- checked as a function
        // definition/call, not as a bare substring, so that explanatory comment does not self-fail.
        Assert.DoesNotContain("function applyFailureShake", js);
        Assert.DoesNotContain("applyFailureShake(", js);
        Assert.DoesNotContain("nebulaCanvas", js);
        Assert.DoesNotContain("requestFullscreen", js);
        Assert.DoesNotContain("addEventListener(\"keydown\"", js);
    }

    /// <summary>
    /// T9b (webview-alert-layer): the page must idle with NOTHING running -- no draw, no
    /// requestAnimationFrame loop -- until a "show" message arrives, so a preloaded-but-hidden
    /// layer (T9c) costs ~0% GPU while idle (feature doc, "Idle cost"). Loading the file bare (no
    /// hash/query) must not auto-start a warning any more, unlike the one-shot page T1 shipped.
    /// </summary>
    [Fact]
    public void Script_IdlesWithNoAnimationUntilAShowMessageArrives()
    {
        var js = ReadShipped("alert-layer.js");
        Assert.Contains("hasExplicitParams", js);
        Assert.Contains("function startShowing", js);
        Assert.Contains("function hide(", js);
        Assert.Contains("if (!animating) return;", js);
    }

    /// <summary>
    /// T9b: the host (T9c) drives a preloaded page by posting <c>{type:"show",...}</c>/
    /// <c>{type:"hide"}</c> through the WebView2 message bridge, guarded the same way the outgoing
    /// side already is so this file keeps working when opened directly in a browser tab.
    /// </summary>
    [Fact]
    public void Script_HandlesShowAndHideMessagesFromTheHost()
    {
        var js = ReadShipped("alert-layer.js");
        Assert.Contains("addEventListener(\"message\"", js);
        Assert.Contains("\"show\"", js);
        Assert.Contains("\"hide\"", js);
        Assert.Contains("data.kind", js);
        Assert.Contains("data.duration", js);
    }

    /// <summary>
    /// T9b: the host needs to know the navigated page produced a live, running script -- not just
    /// that navigation completed -- before it trusts a pending "show" was actually received.
    /// </summary>
    [Fact]
    public void Script_PostsReadyOnceInitialised()
    {
        var js = ReadShipped("alert-layer.js");
        Assert.Contains("\"ready\"", js);
    }

    /// <summary>
    /// alert-tile-mosaic (2026-09-26): structural guards only, matching every other assertion in this
    /// file -- there is no DOM/canvas test harness in this repo (feature doc, "JS has no test
    /// harness"), so pixel-accurate rendering stays a hardware check (T4). These at least prove the
    /// tile-layout arithmetic, the mixed-kind state tracking, and the new message/hash shapes exist
    /// in the shipped file, catching an accidental deletion or rename a build-output copy check alone
    /// would miss.
    /// </summary>
    [Fact]
    public void Script_ComputesPerTileRectsWithOuterAndInnerGap()
    {
        var js = ReadShipped("alert-layer.js");
        Assert.Contains("function tileRects", js);
        Assert.Contains("gapPx", js);
        Assert.Contains("gridColumns", js);
        Assert.Contains("gridRows", js);
        // Gap arrives in physical pixels; converted to CSS pixels with the same devicePixelRatio-
        // derived scale resize() already uses to size the canvas (feature doc, T3 approach).
        Assert.Contains("gapPx / canvasScaleX", js);
        Assert.Contains("gapPx / canvasScaleY", js);
    }

    /// <summary>A single tile must look exactly like the old full-screen layer: no outer gap applied.</summary>
    [Fact]
    public void Script_SingleTileSkipsTheOuterGap()
    {
        var js = ReadShipped("alert-layer.js");
        Assert.Contains("tiles.length <= 1", js);
    }

    /// <summary>A mosaic can show failed and warning tiles together, each with its own shake/reveal timing -- tracked per KIND, not per tile-instance.</summary>
    [Fact]
    public void Script_TracksShakeAndRevealStatePerKindNotPerTileInstance()
    {
        var js = ReadShipped("alert-layer.js");
        Assert.Contains("kindState", js);
        Assert.Contains("function advanceKindState", js);
    }

    /// <summary>The host posts the new {type:"show", tiles, columns, rows, gap, duration} shape; the old {kind} shape still works for a manual check.</summary>
    [Fact]
    public void Script_HandlesTheTilesGridAndGapMessageFromTheHostAndTheOldKindShape()
    {
        var js = ReadShipped("alert-layer.js");
        Assert.Contains("data.tiles", js);
        Assert.Contains("data.columns", js);
        Assert.Contains("data.rows", js);
        Assert.Contains("data.gap", js);
        Assert.Contains("data.kind", js);
    }

    /// <summary>The hash API gains tiles=..&amp;columns=..&amp;rows=..&amp;gap=..; the old kind= form still works.</summary>
    [Fact]
    public void Script_HashApiAcceptsTilesColumnsRowsAndGap()
    {
        var js = ReadShipped("alert-layer.js");
        Assert.Contains("params.has(\"tiles\")", js);
        Assert.Contains("params.get(\"columns\")", js);
        Assert.Contains("params.get(\"rows\")", js);
        Assert.Contains("params.get(\"gap\")", js);
        Assert.Contains("params.get(\"kind\")", js);
    }

    /// <summary>
    /// T7 (alert-tile-mosaic, 2026-09-26): "lay the N&gt;1 mosaic out inside the work area" -- the
    /// host posts a work area (physical pixels, relative to the layer surface) in the "show" message;
    /// the page converts it to CSS pixels with the same devicePixelRatio-derived scale it already
    /// uses for the gap, and falls back to the whole canvas when it is missing or degenerate (zero or
    /// negative width/height), exactly the pre-T7 full-canvas behaviour. N=1 stays untouched --
    /// <see cref="Script_SingleTileSkipsTheOuterGap"/> already proves that branch is unchanged.
    /// Structural guards only, matching every other assertion in this file (no DOM/canvas harness).
    /// </summary>
    [Fact]
    public void Script_LaysTheGridOutInsideTheWorkAreaWithFallbackToTheFullCanvas()
    {
        var js = ReadShipped("alert-layer.js");
        Assert.Contains("function gridAreaRect", js);
        Assert.Contains("workAreaWidth", js);
        Assert.Contains("workAreaHeight", js);
        Assert.Contains("workAreaLeft", js);
        Assert.Contains("workAreaTop", js);
        // Converted with the SAME devicePixelRatio-derived scale the gap already uses.
        Assert.Contains("workAreaLeft / canvasScaleX", js);
        Assert.Contains("workAreaTop / canvasScaleY", js);
        Assert.Contains("workAreaWidth / canvasScaleX", js);
        Assert.Contains("workAreaHeight / canvasScaleY", js);
    }

    /// <summary>The host's "show" message and the hash API both gain the work area (T7).</summary>
    [Fact]
    public void Script_HandlesTheWorkAreaFromTheHostMessageAndTheHashApi()
    {
        var js = ReadShipped("alert-layer.js");
        Assert.Contains("data.workArea", js);
        Assert.Contains("params.get(\"work\")", js);
    }
}
