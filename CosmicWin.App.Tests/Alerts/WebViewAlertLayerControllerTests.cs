using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using CosmicWin.Interop.Win32;
using CosmicWin.App;
using CosmicWin.App.Alerts;

namespace CosmicWin.App.Tests.Alerts;

public sealed class WebViewAlertLayerControllerTests
{
    [Fact]
    public void ConstructorAcceptsOwningStaWithoutInstalledSynchronizationContext()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(null);
                var dispatcher = Dispatcher.CurrentDispatcher;
                using var host = new Win32VideoWallpaperHost();
                using var layer = new WebViewAlertLayerController(host);
                Assert.Null(SynchronizationContext.Current);
                Assert.Throws<InvalidOperationException>(() =>
                    Task.Run(() => layer.End()).GetAwaiter().GetResult());
                Assert.Same(dispatcher, Dispatcher.FromThread(Thread.CurrentThread));
            }
            catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(error);
    }

    [Fact]
    public void TransparencyIsConfiguredPerControllerWithoutMutatingProcessEnvironment()
    {
        // Structural guard only: this does not exercise WebView2's native transparency behavior.
        var source = ReadControllerSource();
        Assert.DoesNotContain("SetEnvironmentVariable", source);
        Assert.DoesNotContain("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", source);
        Assert.Contains("CreateCoreWebView2ControllerOptions()", source);
        Assert.Contains("DefaultBackgroundColor = Color.Transparent", source);
        Assert.Contains("CreateCoreWebView2CompositionControllerAsync(hwnd, options)", source);
    }

    /// <summary>
    /// T9a: every currently-silent <c>Debug.WriteLine</c> catch site, the silent "no overlay visual"
    /// path, and the WebView2-specific lifecycle events (create start/ready, navigation, process
    /// failure, close-with-reason) must ALSO call <see cref="AlertLayerTrace"/> -- these only fire
    /// once a real WebView2 environment/controller exists, which stays a hardware-only concern (see
    /// the class remarks), so this is a structural guard rather than a behavioral one, matching
    /// <see cref="TransparencyIsConfiguredPerControllerWithoutMutatingProcessEnvironment"/> above.
    /// </summary>
    [Fact]
    public void EveryLifecycleEventAndSilentFailurePathIsTraced()
    {
        var source = ReadControllerSource();
        Assert.Contains("AlertLayerTrace.CreateStart(", source);
        Assert.Contains("AlertLayerTrace.EnvironmentReady(", source);
        Assert.Contains("AlertLayerTrace.ControllerReady(", source);
        Assert.Contains("AlertLayerTrace.NavigationCompleted(", source);
        Assert.Contains("AlertLayerTrace.ProcessFailed(", source);
        Assert.Contains("AlertLayerTrace.Done()", source);
        Assert.Contains("AlertLayerTrace.NoOverlayVisual()", source);
        Assert.Contains("candidate.CoreWebView2.ProcessFailed += OnProcessFailed;", source);
        Assert.Contains("candidate.CoreWebView2.NavigationCompleted += OnNavigationCompleted;", source);
        // Every Debug.WriteLine catch site must be paired with an AlertLayerTrace.Error/Close call on
        // the very next non-blank line -- proving telemetry was added ALONGSIDE, not instead of, the
        // existing debugger-only diagnostic.
        var lines = source.Split('\n');
        var debugWriteLineSites = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("Debug.WriteLine(")) continue;
            debugWriteLineSites++;
            var pairedWithTrace = lines[i].Contains("AlertLayerTrace.Error(")
                || (i + 1 < lines.Length && lines[i + 1].Contains("AlertLayerTrace"));
            Assert.True(pairedWithTrace, $"Line {i + 1} ('{lines[i].Trim()}') has no paired AlertLayerTrace call.");
        }
        Assert.True(debugWriteLineSites >= 8, $"Expected at least 8 Debug.WriteLine sites, found {debugWriteLineSites}.");
    }

    /// <summary>
    /// T7 (alert-tile-mosaic, 2026-09-26): the "show" message must also carry the work area
    /// (<see cref="AlertShowRequest.WorkAreaLeft"/> etc.) so the page can lay an N&gt;1 mosaic out
    /// inside it -- <c>PostShow</c>'s real WebView2 call stays a hardware-only concern (see the class
    /// remarks), so this is a structural guard on the JSON literal, matching every other assertion in
    /// this file that reads the source directly.
    /// </summary>
    [Fact]
    public void PostShowIncludesTheWorkAreaInTheShowMessage()
    {
        var source = ReadControllerSource();
        var start = source.IndexOf("private void PostShow(AlertShowRequest request)", StringComparison.Ordinal);
        Assert.True(start >= 0, "expected a private void PostShow(AlertShowRequest request) method");
        var next = source.IndexOf("\n    private", start + 1, StringComparison.Ordinal);
        var body = source[start..(next > 0 ? next : source.Length)];
        Assert.Contains("workArea", body);
        Assert.Contains("request.WorkAreaLeft", body);
        Assert.Contains("request.WorkAreaTop", body);
        Assert.Contains("request.WorkAreaWidth", body);
        Assert.Contains("request.WorkAreaHeight", body);
    }

    private static string ReadControllerSource([CallerFilePath] string testFilePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!,
            "..", "..", "CosmicWin.App", "Alerts", "WebViewAlertLayerController.cs")));

    /// <summary>
    /// T9c (webview-alert-layer): <c>Preload</c>/<c>Start</c>/<c>End</c> against an UNATTACHED host
    /// (Hwnd == 0, IsCompositionReady == false) never reach a real WebView2 -- same trick as T9a's
    /// show/hide test -- so this proves the pure wiring (trace lines, no teardown ever attempted)
    /// without hardware. Real preload/recreate/backoff behavior stays T9e.
    /// </summary>
    [Fact]
    public void PreloadStartAndEndNeverReachWebView2WhenTheHostIsUnattached()
    {
        var traces = new List<string>();
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                using var host = new Win32VideoWallpaperHost();
                using var layer = new WebViewAlertLayerController(host, trace: line => { lock (traces) traces.Add(line); });
                layer.Preload();
                layer.Start(new AlertShowRequest(["warning"], 1, 1, 8, 1000));
                layer.End();
                layer.Start(new AlertShowRequest(["failed", "failed"], 2, 1, 8, 500));
                layer.End();
            }
            catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(error);
        Assert.Contains("alert-layer show tiles=warning grid=1x1 gap=8 work=0,0,0x0 duration=1000", traces);
        Assert.Contains("alert-layer show tiles=failed,failed grid=2x1 gap=8 work=0,0,0x0 duration=500", traces);
        Assert.Equal(2, traces.Count(line => line == "alert-layer hide"));
        Assert.DoesNotContain(traces, line => line.StartsWith("alert-layer close", StringComparison.Ordinal));
    }

    /// <summary>
    /// T9c: <c>End</c> only posts a "hide" message and hides the layer -- it must never tear down
    /// the controller/environment (that only happens on Dispose, a host identity change, or a real
    /// creation/process failure). This is the persistent-preload model's core difference from the old
    /// per-alert create/dispose controller.
    /// </summary>
    [Fact]
    public void EndOnlyHidesThePageAndNeverTearsDownTheController()
    {
        var source = ReadControllerSource();
        var start = source.IndexOf("private void End(string reason)", StringComparison.Ordinal);
        Assert.True(start >= 0, "expected a private void End(string reason) method");
        var next = new[]
            {
                source.IndexOf("\n    private", start + 1, StringComparison.Ordinal),
                source.IndexOf("\n    public", start + 1, StringComparison.Ordinal),
            }
            .Where(i => i > 0).DefaultIfEmpty(source.Length).Min();
        var body = source[start..next];
        Assert.DoesNotContain("TearDown(", body);
        Assert.Contains("PostWebMessageAsJson", body);
        Assert.Contains("type\\\":\\\"hide", body); // the JSON literal's escaped quotes, as they appear in source
        Assert.Contains("IsVisible = false", body);
    }

    /// <summary>
    /// T9c: a preloaded page is navigated ONCE, with no kind/duration in the URL -- it starts idle
    /// and is driven entirely by later show/hide messages (T9b). The old per-alert hash-based
    /// navigation must be gone.
    /// </summary>
    [Fact]
    public void PreloadNavigatesTheBarePageWithNoKindOrDurationHash()
    {
        var source = ReadControllerSource();
        Assert.Contains("Navigate(\"https://cosmicwin-alert.example/alert-layer.html\")", source);
        Assert.DoesNotContain("#kind=", source);
        Assert.Contains("PostWebMessageAsJson", source);
    }

    /// <summary>
    /// The virtual host must use an RFC 6761 reserved TLD, never <c>.local</c>. The WebView2
    /// reference for <c>SetVirtualHostNameToFolderMapping</c>: "using .local as the top-level domain
    /// name will work but can cause a delay during navigations. You should avoid using .local if
    /// you can." Every controller creation (startup and every recovery) pays that navigation.
    /// </summary>
    [Fact]
    public void VirtualHostUsesAReservedExampleDomainNotDotLocal()
    {
        var source = ReadControllerSource();
        Assert.Contains("SetVirtualHostNameToFolderMapping(\"cosmicwin-alert.example\"", source);
        Assert.DoesNotContain(".local\"", source);
        Assert.DoesNotContain(".local/", source);
    }

    /// <summary>
    /// T9c: host identity changes (Explorer restart) and creation/process failures must both drive
    /// the SAME pure backoff/recreate decision (<see cref="AlertLayerPreloadState"/>, tested directly
    /// in <c>AlertLayerPreloadStateTests</c>), and the environment is dropped ONLY for a real
    /// creation/process failure -- not for an ordinary host change (feature doc, "Keep _environment
    /// for the process lifetime unless creation failed/process failed").
    /// </summary>
    [Fact]
    public void RecreatesOnHostChangeOrFailureAndOnlyDropsTheEnvironmentOnARealFailure()
    {
        var source = ReadControllerSource();
        Assert.Contains("_state.HostChanged(", source);
        Assert.Contains("_state.CanCreate", source);
        Assert.Contains("_state.Failed()", source);
        Assert.Contains("_state.Created()", source);
        Assert.Contains("TearDown(\"create-failed\", dropEnvironment: true)", source);
        Assert.Contains("TearDown(\"process-failed\", dropEnvironment: true)", source);
        Assert.Contains("TearDown(\"host-changed\")", source);
        Assert.DoesNotContain("TearDown(\"host-changed\", dropEnvironment: true)", source);
    }

    /// <summary>
    /// D3 (html-wallpaper-demo, demo/html-wallpaper-d3-switch): a controller built in html wallpaper
    /// mode must map and navigate to the active SCENE page (shipped by the csproj's own
    /// <c>Wallpaper\Web\**</c> Content item) instead of the alert-only page, under its own reserved
    /// example domain -- the video-mode literals proven by
    /// <see cref="VirtualHostUsesAReservedExampleDomainNotDotLocal"/> and
    /// <see cref="PreloadNavigatesTheBarePageWithNoKindOrDurationHash"/> above must still be present
    /// UNCHANGED, since video mode must behave exactly as it did before D3.
    /// <para>
    /// D6a: the mapping now covers the WHOLE <c>Wallpaper\Web</c> folder (every scene page now loads
    /// <c>Wallpaper\Web\shared\...</c> siblings).
    /// </para>
    /// <para>
    /// D6d: the navigated URL's scene segment comes from <see cref="SceneFolderName"/> (see
    /// <see cref="SceneFolderName_MapsEachSceneToItsFixedFolderName"/> for that pure mapping's own
    /// coverage), not a hardcoded constant, and carries the configured `fps` cap as a query param.
    /// </para>
    /// </summary>
    [Fact]
    public void HtmlWallpaperMode_MapsAndNavigatesToTheConfiguredScenePageUnderItsOwnDomain()
    {
        var source = ReadControllerSource();

        // Video mode, untouched.
        Assert.Contains("SetVirtualHostNameToFolderMapping(\"cosmicwin-alert.example\"", source);
        Assert.Contains("Navigate(\"https://cosmicwin-alert.example/alert-layer.html\")", source);

        // Html wallpaper mode, D6d.
        Assert.Contains("SetVirtualHostNameToFolderMapping(\"cosmicwin-scene.example\"", source);
        Assert.Contains("var sceneFolder = SceneFolderName(_htmlWallpaperScene);", source);
        Assert.Contains(
            "$\"https://cosmicwin-scene.example/{sceneFolder}/index.html?fps={_htmlWallpaperFps}\"",
            source);
        Assert.Contains("\"Wallpaper\", \"Web\")", source);
        Assert.DoesNotContain("\"Wallpaper\", \"Web\", \"processing\"", source);
        Assert.DoesNotContain(".local\"", source);
        Assert.DoesNotContain(".local/", source);
    }

    /// <summary>
    /// D6d (html-wallpaper-demo, wallpaper-scene setting): <see
    /// cref="WebViewAlertLayerController.SceneFolderName"/> is the ONLY place a
    /// <see cref="WallpaperScene"/> value becomes a folder name that reaches the Navigate URL -- a
    /// pure, closed mapping over a compile-time-fixed enum, so this is a real behavioural test (not a
    /// source-text guard like most of this file, which exists only because the rest of this class
    /// needs a live WebView2 to exercise).
    /// </summary>
    [Theory]
    [InlineData(WallpaperScene.Processing, "processing")]
    [InlineData(WallpaperScene.Explorer, "explorer")]
    [InlineData(WallpaperScene.Idle, "idle")]
    [InlineData(WallpaperScene.Raphael, "raphael")]
    public void SceneFolderName_MapsEachSceneToItsFixedFolderName(WallpaperScene scene, string expectedFolder)
    {
        Assert.Equal(expectedFolder, WebViewAlertLayerController.SceneFolderName(scene));
    }

    /// <summary>
    /// D3: the visibility decisions in <c>TryMarkReady</c>, <c>End</c>, and the page's "done" message
    /// must be driven by <see cref="WebViewAlertLayerVisibility"/>, not an inline mode check -- proven
    /// structurally here, the same way every other WebView2-only behaviour in this class is (see the
    /// class remarks).
    /// </summary>
    [Fact]
    public void VisibilityDecisions_GoThroughTheSharedPolicyClass()
    {
        var source = ReadControllerSource();

        Assert.Contains("WebViewAlertLayerVisibility.ShowOnReady(", source);
        Assert.Contains("WebViewAlertLayerVisibility.HideOnEndOrDone(", source);

        var endStart = source.IndexOf("private void End(string reason)", StringComparison.Ordinal);
        Assert.True(endStart >= 0);
        var endNext = new[]
            {
                source.IndexOf("\n    private", endStart + 1, StringComparison.Ordinal),
                source.IndexOf("\n    public", endStart + 1, StringComparison.Ordinal),
            }
            .Where(i => i > 0).DefaultIfEmpty(source.Length).Min();
        var endBody = source[endStart..endNext];
        Assert.Contains("WebViewAlertLayerVisibility.HideOnEndOrDone(", endBody);
        // Still present -- the guard wraps it, it does not replace it (video mode is unchanged).
        Assert.Contains("IsVisible = false", endBody);
    }
}
