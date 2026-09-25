using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using CosmicWin.Interop.Win32;
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
                layer.Start("warning", 1000);
                layer.End();
                layer.Start("failed", 500);
                layer.End();
            }
            catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(error);
        Assert.Contains("alert-layer show kind=warning duration=1000", traces);
        Assert.Contains("alert-layer show kind=failed duration=500", traces);
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
        Assert.Contains("Navigate(\"https://cosmicwin-alert.local/alert-layer.html\")", source);
        Assert.DoesNotContain("#kind=", source);
        Assert.Contains("PostWebMessageAsJson", source);
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

}
