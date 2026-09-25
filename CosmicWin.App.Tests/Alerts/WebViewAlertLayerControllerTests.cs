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
    /// T9a: <see cref="WebViewAlertLayerController.Start"/>/<c>End</c> trace "show"/"hide"
    /// UNCONDITIONALLY, so this much of the telemetry is exercised without a real WebView2 -- the
    /// host stays unattached here on purpose (Hwnd == 0, IsCompositionReady == false), so Poll()
    /// bails out before ever reaching CreateAsync's WebView2-specific tracing (see the next test).
    /// </summary>
    [Fact]
    public void StartAndEndTraceShowAndHideWithoutEverReachingWebView2()
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
                layer.Start("warning", 1000);
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
        Assert.Contains("alert-layer hide", traces);
        // Never reached: the host was never attached, so no real controller ever existed to close.
        Assert.DoesNotContain(traces, line => line.StartsWith("alert-layer close", StringComparison.Ordinal));
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

    [Fact]
    public async Task DoneAndDeadlineCloseProductionLifetime()
    {
        var closed = 0;
        var now = DateTimeOffset.UtcNow;
        using var lifetime = new AlertLayerLifecycle(() => now);
        lifetime.Start(100);
        await lifetime.CreateAsync(1, 2, () => Task.FromResult<IDisposable?>(new CallbackDisposable(() => closed++)));
        lifetime.Done();
        Assert.Equal(1, closed);
        lifetime.Start(100);
        await lifetime.CreateAsync(1, 2, () => Task.FromResult<IDisposable?>(new CallbackDisposable(() => closed++)));
        now = now.AddMilliseconds(2101);
        Assert.False(lifetime.Poll(1, 2));
        Assert.Equal(2, closed);
    }

    [Fact]
    public async Task FailureBacksOffAndInvalidationClosesLateCreation()
    {
        var now = DateTimeOffset.UtcNow;
        var closed = 0;
        using var lifetime = new AlertLayerLifecycle(() => now);
        lifetime.Start(1000);
        await lifetime.CreateAsync(1, 2, () => throw new InvalidOperationException());
        Assert.False(lifetime.CanCreate);
        now = now.AddMilliseconds(251);
        Assert.False(lifetime.CanCreate);
        now = now.AddSeconds(2);
        Assert.True(lifetime.CanCreate);
        var pending = new TaskCompletionSource<IDisposable?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var creation = lifetime.CreateAsync(1, 2, () => pending.Task);
        lifetime.Poll(3, 2);
        pending.SetResult(new CallbackDisposable(() => closed++));
        await creation;
        Assert.Equal(1, closed);
        Assert.False(lifetime.IsActive);
    }

    [Fact]
    public async Task NullCreationResultBacksOffThroughProductionLifecycle()
    {
        var now = DateTimeOffset.UtcNow;
        using var lifetime = new AlertLayerLifecycle(() => now);
        lifetime.Start(1000);
        await lifetime.CreateAsync(1, 2, () => Task.FromResult<IDisposable?>(null));
        Assert.False(lifetime.IsActive);
        Assert.False(lifetime.CanCreate);
        now = now.AddMilliseconds(500);
        Assert.True(lifetime.CanCreate);
    }

    private sealed class CallbackDisposable(Action callback) : IDisposable
    {
        public void Dispose() => callback();
    }
}
