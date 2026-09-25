using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using CosmicWin.Interop.Win32;
using Microsoft.Web.WebView2.Core;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace CosmicWin.App.Alerts;

/// <summary>
/// Permanently preloaded WebView2 composition layer. Construct on the owning STA; call <see
/// cref="Preload"/> once its dispatcher is pumping, then drive it with <see cref="Start"/>/<see
/// cref="End"/> per alert.
/// </summary>
/// <remarks>
/// T9c (webview-alert-layer): T6 measured ~2.8s from an alert command to a visible layer under the
/// OLD model -- a fresh WebView2 environment + controller created per alert and disposed when it
/// ended -- plus F1 (the first alert after launch never showing at all). The maintainer's fix
/// (feature doc, "Idle cost (superseded 2026-09-24)") is a PERMANENT PRELOAD: one controller is
/// created at startup and kept alive, hidden, for the process's life; <see cref="Start"/>/<see
/// cref="End"/> only show/hide the already-navigated page. <see cref="AlertLayerPreloadState"/> is
/// the pure state machine behind this (host identity/backoff/readiness/pending-show), unit-tested
/// directly; this class is the thin WebView2-specific shell around it -- real environment/controller/
/// navigation stay a hardware-only concern, same split T3/T6 established for the old per-alert model.
/// <para>
/// Every lifecycle event is traced through the optional <paramref name="trace"/> delegate
/// (production wires <c>desktopTrace.Record</c>, see <c>AppComposition.WireProduction</c>) --
/// <see cref="AlertLayerTrace"/> owns the exact wording and is unit-tested directly.
/// </para>
/// </remarks>
public sealed class WebViewAlertLayerController : IDisposable
{
    private readonly Win32VideoWallpaperHost _host;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _poll;
    private readonly Action<string>? _trace;
    private readonly AlertLayerPreloadState _state;
    private CoreWebView2Environment? _environment;
    private CoreWebView2CompositionController? _controller;
    private int _generation;
    private nint _hwnd;
    private bool _disposed;
    private bool _creating;
    private bool _preloading;
    private bool _navigationCompleted;
    private bool _pageReportedReady;
    private int _epoch;

    // Set right before Navigate() so OnNavigationCompleted can report elapsed time since THAT call,
    // not since the whole creation started (environment/controller creation already has its own
    // separately-traced timings).
    private Stopwatch? _navigateStopwatch;

    public WebViewAlertLayerController(Win32VideoWallpaperHost host, Action<string>? trace = null, Func<DateTimeOffset>? clock = null)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("A WPF UI STA is required.");
        _host = host;
        _trace = trace;
        _state = new AlertLayerPreloadState(clock);
        _dispatcher = Dispatcher.CurrentDispatcher;
        _poll = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background,
            (_, _) => Poll(), _dispatcher);
    }

    /// <summary>
    /// Begins keeping one WebView2 controller alive PERMANENTLY: creates the environment/composition
    /// controller, adds the overlay visual, and navigates to the bare (idle) alert page, hidden. The
    /// 250ms poll then keeps running for the process's life -- not just while an alert is active --
    /// watching for a host identity change (Explorer restart) or a lost composition surface, and
    /// recreating (with the existing exponential backoff) when either happens. Idempotent.
    /// </summary>
    public void Preload()
    {
        CheckAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_preloading) return;
        _preloading = true;
        _poll.Start();
        Poll();
    }

    public void Start(string kind, int durationMilliseconds)
    {
        CheckAccess();
        if (SynchronizationContext.Current is not DispatcherSynchronizationContext)
            throw new InvalidOperationException("A pumped WPF UI STA is required to start the alert layer.");
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (kind is not ("warning" or "failed")) throw new ArgumentOutOfRangeException(nameof(kind));
        if (durationMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(durationMilliseconds));
        _trace?.Invoke(AlertLayerTrace.Show(kind, durationMilliseconds));
        // Safety net: the queue must never lose a Start because production forgot to call Preload
        // first. Calling it here when it was already called is a no-op.
        Preload();
        // Ready while already visible still re-shows (a fresh Start must never be swallowed as a
        // no-op): AlertLayerPreloadState.RequestShow always returns a tuple while Ready, regardless
        // of Visible -- see its own tests.
        if (_state.RequestShow(kind, durationMilliseconds) is { } show) PostShow(show.Kind, show.DurationMilliseconds);
    }

    public void End() => End("end");

    private void End(string reason)
    {
        CheckAccess();
        _trace?.Invoke(AlertLayerTrace.Hide());
        _state.Hide();
        if (_controller is null) return;
        try
        {
            _controller.CoreWebView2.PostWebMessageAsJson("{\"type\":\"hide\"}");
            _controller.IsVisible = false;
        }
        catch (Exception ex) { Debug.WriteLine(ex); _trace?.Invoke(AlertLayerTrace.Error($"end-{reason}", ex)); }
    }

    private void PostShow(string kind, int durationMilliseconds)
    {
        if (_controller is null) return;
        try
        {
            _controller.CoreWebView2.PostWebMessageAsJson(
                $"{{\"type\":\"show\",\"kind\":\"{kind}\",\"duration\":{durationMilliseconds}}}");
            _controller.IsVisible = true;
        }
        catch (Exception ex) { Debug.WriteLine(ex); _trace?.Invoke(AlertLayerTrace.Error("post-show", ex)); }
    }

    private void Poll()
    {
        if (!_preloading) return;
        try
        {
            var hwnd = _host.Hwnd;
            var generation = _host.CompositionGeneration;
            if (_state.HostChanged(hwnd, generation) && _controller is not null)
            {
                TearDown("host-changed");
            }
            if (!_host.IsCompositionReady || hwnd == 0)
            {
                if (_controller is not null) TearDown("host-not-ready");
                return;
            }
            if (_controller is null && !_creating && _state.CanCreate)
                _ = CreateAsync(hwnd, generation);
            else if (_controller is not null)
                Resize();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            _trace?.Invoke(AlertLayerTrace.Error("poll", ex));
            _state.Failed();
            TearDown("poll-failed");
        }
    }

    private async Task CreateAsync(nint hwnd, int generation)
    {
        _creating = true;
        _navigationCompleted = false;
        _pageReportedReady = false;
        var epoch = _epoch;
        var stopwatch = Stopwatch.StartNew();
        _trace?.Invoke(AlertLayerTrace.CreateStart(hwnd, generation));
        CoreWebView2CompositionController? candidate = null;
        bool visualAdded = false;
        try
        {
            _environment ??= await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CosmicWin", "WebView2Alerts"));
            _trace?.Invoke(AlertLayerTrace.EnvironmentReady(stopwatch.ElapsedMilliseconds));
            if (!StillCurrent(epoch, hwnd, generation)) return;
            var options = _environment.CreateCoreWebView2ControllerOptions();
            options.DefaultBackgroundColor = Color.Transparent;
            candidate = await _environment.CreateCoreWebView2CompositionControllerAsync(hwnd, options);
            _trace?.Invoke(AlertLayerTrace.ControllerReady(stopwatch.ElapsedMilliseconds));
            if (!StillCurrent(epoch, hwnd, generation)) return;
            candidate.DefaultBackgroundColor = Color.Transparent;
            var visual = _host.AddCompositionOverlayVisual();
            if (visual is null)
            {
                _trace?.Invoke(AlertLayerTrace.NoOverlayVisual());
                _state.Failed();
                return;
            }
            visualAdded = true;
            if (!StillCurrent(epoch, hwnd, generation)) return;
            candidate.RootVisualTarget = visual;
            _host.CommitComposition();
            candidate.CoreWebView2.SetVirtualHostNameToFolderMapping("cosmicwin-alert.local",
                Path.Combine(AppContext.BaseDirectory, "Alerts", "Web"), CoreWebView2HostResourceAccessKind.DenyCors);
            candidate.CoreWebView2.WebMessageReceived += OnMessage;
            candidate.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            candidate.CoreWebView2.ProcessFailed += OnProcessFailed;
            _controller = candidate;
            candidate = null;
            visualAdded = false;
            _hwnd = hwnd;
            _generation = generation;
            Resize();
            // Preloaded and hidden -- show/hide is driven entirely by Start/End posting messages
            // once the page reports itself ready (see OnNavigationCompleted/OnMessage below).
            _controller.IsVisible = false;
            _state.Created();
            _navigateStopwatch = Stopwatch.StartNew();
            // No kind/duration hash any more (T9b): the page loads idle and is driven by show/hide
            // messages once it is ready.
            _controller.CoreWebView2.Navigate("https://cosmicwin-alert.local/alert-layer.html");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WebView alert creation failed: {ex}");
            _trace?.Invoke(AlertLayerTrace.Error("create", ex));
            _state.Failed();
            TearDown("create-failed", dropEnvironment: true);
        }
        finally
        {
            try { candidate?.Close(); }
            catch (Exception ex) { Debug.WriteLine(ex); _trace?.Invoke(AlertLayerTrace.Error("create-cleanup-controller", ex)); }
            if (visualAdded)
            {
                try { _host.RemoveCompositionOverlayVisual(); _host.CommitComposition(); }
                catch (Exception ex) { Debug.WriteLine(ex); _trace?.Invoke(AlertLayerTrace.Error("create-cleanup-visual", ex)); }
            }
            _creating = false;
        }
    }

    /// <summary>Whether an in-flight <see cref="CreateAsync"/> call is still the one that started it -- a host change or Dispose during an await bumps <see cref="_epoch"/> and invalidates it.</summary>
    private bool StillCurrent(int epoch, nint hwnd, int generation) =>
        epoch == _epoch && !_disposed && _preloading &&
        _host.IsCompositionReady && _host.Hwnd == hwnd && _host.CompositionGeneration == generation;

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        try
        {
            _trace?.Invoke(AlertLayerTrace.NavigationCompleted(
                args.IsSuccess, args.WebErrorStatus, _navigateStopwatch?.ElapsedMilliseconds ?? 0));
            if (!args.IsSuccess)
            {
                _state.Failed();
                TearDown("navigation-failed");
                return;
            }
            _navigationCompleted = true;
            TryMarkReady();
        }
        catch (Exception ex) { Debug.WriteLine(ex); _trace?.Invoke(AlertLayerTrace.Error("navigation-completed", ex)); }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs args)
    {
        try
        {
            _trace?.Invoke(AlertLayerTrace.ProcessFailed(args.ProcessFailedKind, args.Reason));
            _state.Failed();
            TearDown("process-failed", dropEnvironment: true);
        }
        catch (Exception ex) { Debug.WriteLine(ex); _trace?.Invoke(AlertLayerTrace.Error("process-failed", ex)); }
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            var message = args.TryGetWebMessageAsString();
            if (message == "ready")
            {
                _pageReportedReady = true;
                TryMarkReady();
            }
            else if (message == "done")
            {
                _trace?.Invoke(AlertLayerTrace.Done());
                // The page reports nothing else: the QUEUE owns ending the alert (AppComposition
                // calls End() itself once it advances) -- this only reflects the page's own state.
                _state.PageDone();
                if (_controller is not null) _controller.IsVisible = false;
            }
        }
        catch (Exception ex) { Debug.WriteLine(ex); _trace?.Invoke(AlertLayerTrace.Error("message", ex)); }
    }

    /// <summary>Ready = navigation completed AND the page's own "ready" message received (feature doc). Applies any show that arrived while not yet ready, with its REMAINING duration.</summary>
    private void TryMarkReady()
    {
        if (!_navigationCompleted || !_pageReportedReady) return;
        _state.MarkReady();
        if (_state.ApplyPendingShowIfDue() is { } pending) PostShow(pending.Kind, pending.DurationMilliseconds);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint hwnd, out RECT rect);

    private void Resize()
    {
        if (_controller is not null && GetClientRect(_hwnd, out var rect))
            _controller.Bounds = new Rectangle(0, 0, rect.right - rect.left, rect.bottom - rect.top);
    }

    /// <summary>
    /// Tears down the controller/environment -- unlike the old per-alert model, this is NOT what
    /// <see cref="End"/> does. It only ever runs on <see cref="Dispose"/>, a host identity change, a
    /// lost composition surface, or a real creation/process/navigation failure. <paramref
    /// name="dropEnvironment"/> is true ONLY for a real creation/process failure (feature doc: "Keep
    /// _environment for the process lifetime unless creation failed/process failed") -- an ordinary
    /// host change reuses the same environment for the next controller.
    /// </summary>
    private void TearDown(string reason, bool dropEnvironment = false)
    {
        _state.ControllerLost();
        ++_epoch;
        _navigationCompleted = false;
        _pageReportedReady = false;
        if (dropEnvironment) _environment = null;
        var old = _controller;
        _controller = null;
        if (old is null) return;
        _trace?.Invoke(AlertLayerTrace.Close(reason));
        try { old.CoreWebView2.WebMessageReceived -= OnMessage; }
        catch (Exception ex) { Debug.WriteLine(ex); _trace?.Invoke(AlertLayerTrace.Error("close-unsubscribe-message", ex)); }
        try { old.CoreWebView2.NavigationCompleted -= OnNavigationCompleted; }
        catch (Exception ex) { Debug.WriteLine(ex); _trace?.Invoke(AlertLayerTrace.Error("close-unsubscribe-navigation", ex)); }
        try { old.CoreWebView2.ProcessFailed -= OnProcessFailed; }
        catch (Exception ex) { Debug.WriteLine(ex); _trace?.Invoke(AlertLayerTrace.Error("close-unsubscribe-process-failed", ex)); }
        try { old.Close(); }
        catch (Exception ex) { Debug.WriteLine(ex); _trace?.Invoke(AlertLayerTrace.Error("close-controller", ex)); }
        try { _host.RemoveCompositionOverlayVisual(); _host.CommitComposition(); }
        catch (Exception ex) { Debug.WriteLine(ex); _trace?.Invoke(AlertLayerTrace.Error("close-visual", ex)); }
    }

    private void CheckAccess()
    {
        if (!_dispatcher.CheckAccess()) throw new InvalidOperationException("Use the owning UI dispatcher.");
    }

    public void Dispose()
    {
        CheckAccess();
        if (_disposed) return;
        _poll.Stop();
        _state.Hide();
        TearDown("disposed", dropEnvironment: true);
        _disposed = true;
    }
}
