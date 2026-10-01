using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using ICompositionOverlaySurface = CosmicWin.Interop.ICompositionOverlaySurface;
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
    private readonly ICompositionOverlaySurface _host;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _poll;
    private readonly Action<string>? _trace;
    private readonly AlertLayerPreloadState _state;
    // D3 (html-wallpaper-demo): true only for the demo's html wallpaper mode -- see
    // WebViewAlertLayerVisibility and the mode branches in CreateAsync/TryMarkReady/End/OnMessage.
    // False (the default) reproduces exactly what this class did before D3.
    private readonly bool _htmlWallpaperMode;
    // D6a (html-wallpaper-demo): every scene page shares one virtual host mapping (see the
    // CoreWebView2.SetVirtualHostNameToFolderMapping/Navigate calls below), so only the scene
    // SEGMENT of the navigated URL needs to change to switch scenes.
    // D6d: that segment comes from the wallpaper-scene setting, mapped to its fixed folder name by
    // SceneFolderName -- a closed switch over a compile-time enum, so raw settings text (or anything
    // else) can never reach the Navigate URL as an unvalidated scene segment.
    // S3 (wallpaper-scene-http-endpoint): MUTABLE, unlike every other field seeded from a constructor
    // parameter in this class -- SwitchScene (below) updates it live, after CreateAsync has already
    // navigated once. CreateAsync's own Navigate call reads THIS field, never the constructor's
    // htmlWallpaperScene parameter directly, which is also what makes a later recreate (TearDown from
    // a host change, then Poll calling CreateAsync again) navigate to whatever scene is CURRENT --
    // TearDown never touches this field. Seeded from the constructor's htmlWallpaperScene parameter,
    // still literally "processing" by default (WallpaperScene.Processing), same as before S3.
    private WallpaperScene _currentScene;
    // D6d: caps how many times per second the scene page draws, forwarded to the page as the `fps`
    // query param on the Navigate URL below (shared/js/render-loop.js parses it). 30 or 60, same
    // fixed set Settings.WallpaperFps itself accepts; irrelevant in video mode.
    private readonly int _htmlWallpaperFps;
    // pause-scene-when-covered T2: the DESIRED pause state of the html scene page, kept here (not only
    // posted) because a page that is not ready yet, or is recreated/re-navigated later (Explorer
    // restart, process failure, scene switch), starts out running and must be told again once ready.
    private bool _scenePaused;
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

    public WebViewAlertLayerController(ICompositionOverlaySurface host, Action<string>? trace = null,
        Func<DateTimeOffset>? clock = null, bool htmlWallpaperMode = false,
        WallpaperScene htmlWallpaperScene = WallpaperScene.Processing, int htmlWallpaperFps = 60)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("A WPF UI STA is required.");
        _host = host;
        _trace = trace;
        _htmlWallpaperMode = htmlWallpaperMode;
        _currentScene = htmlWallpaperScene;
        _htmlWallpaperFps = htmlWallpaperFps;
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

    /// <summary>
    /// Shows <paramref name="request"/>'s tiles, laid out in its grid with its gap -- or, while the
    /// page is not yet ready, remembers it as a pending show (see <see
    /// cref="AlertLayerPreloadState.RequestShow"/>).
    /// </summary>
    public void Start(AlertShowRequest request)
    {
        CheckAccess();
        if (SynchronizationContext.Current is not DispatcherSynchronizationContext)
            throw new InvalidOperationException("A pumped WPF UI STA is required to start the alert layer.");
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Tiles.Count == 0)
            throw new ArgumentException("At least one tile is required.", nameof(request));
        if (request.Tiles.Any(tile => tile is not ("warning" or "failed")))
            throw new ArgumentOutOfRangeException(nameof(request), "Every tile must be 'warning' or 'failed'.");
        if (request.Columns <= 0) throw new ArgumentOutOfRangeException(nameof(request), "Columns must be positive.");
        if (request.Rows <= 0) throw new ArgumentOutOfRangeException(nameof(request), "Rows must be positive.");
        if (request.Gap < 0) throw new ArgumentOutOfRangeException(nameof(request), "Gap must not be negative.");
        if (request.DurationMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Duration must be positive.");
        _trace?.Invoke(AlertLayerTrace.Show(request));
        // Safety net: the queue must never lose a Start because production forgot to call Preload
        // first. Calling it here when it was already called is a no-op.
        Preload();
        // Ready while already visible still re-shows (a fresh Start must never be swallowed as a
        // no-op): AlertLayerPreloadState.RequestShow always returns a value while Ready, regardless
        // of Visible -- see its own tests.
        if (_state.RequestShow(request) is { } show) PostShow(show);
    }

    /// <summary>
    /// S3 (wallpaper-scene-http-endpoint): switches the live html-wallpaper scene without tearing
    /// down or recreating the WebView2 controller -- the HTTP route's whole point is a live switch
    /// with no restart. Returns <see langword="false"/> outside <see cref="_htmlWallpaperMode"/>:
    /// there is no scene to switch while the video wallpaper is showing, and the caller
    /// (<c>AppComposition</c>'s HTTP handler) reads that as "answer 503, dispatch nothing".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Decision (feature doc): requesting the CURRENT scene again is a no-op re-navigate but still
    /// returns <see langword="true"/> -- the request was accepted, it simply had nothing to do.
    /// </para>
    /// <para>
    /// When <see cref="_controller"/> has not been created yet (the host is not attached, or <see
    /// cref="CreateAsync"/> has not run), only <see cref="_currentScene"/> is updated: the next <see
    /// cref="CreateAsync"/> call reads it directly, so no separate "pending scene" state is needed.
    /// </para>
    /// </remarks>
    public bool SwitchScene(WallpaperScene scene)
    {
        CheckAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_htmlWallpaperMode)
        {
            return false;
        }

        if (_currentScene == scene)
        {
            return true;
        }

        _currentScene = scene;
        if (_controller is null)
        {
            // Recorded above -- the next CreateAsync call reads _currentScene directly.
            return true;
        }

        // Mirrors the Navigate step of CreateAsync: reset the ready-state flags the SAME way, so
        // OnNavigationCompleted/OnMessage's "ready" handshake runs again for the new page instead of
        // treating this controller as already ready for a page it has not actually loaded yet.
        _navigationCompleted = false;
        _pageReportedReady = false;
        _navigateStopwatch = Stopwatch.StartNew();
        _controller.CoreWebView2.Navigate(SceneUrl(_currentScene, _htmlWallpaperFps));
        return true;
    }

    /// <summary>
    /// pause-scene-when-covered T2: pauses (<paramref name="paused"/> true) or resumes the html wallpaper
    /// scene page while a fullscreen window covers the desktop. Html wallpaper mode only; a no-op in
    /// video mode. Safe at any time: while the page is not ready the state is just remembered and
    /// <see cref="TryMarkReady"/> posts it once the page can receive it.
    /// </summary>
    public void SetScenePaused(bool paused)
    {
        CheckAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_htmlWallpaperMode || _scenePaused == paused) return;
        _scenePaused = paused;
        if (_navigationCompleted && _pageReportedReady) PostScenePause();
    }

    private void PostScenePause()
    {
        if (_controller is null) return;
        try
        {
            _controller.CoreWebView2.PostWebMessageAsJson(
                _scenePaused ? AlertLayerMessages.Pause : AlertLayerMessages.Resume);
        }
        catch (Exception ex) { Debug.WriteLine(ex); _trace?.Invoke(AlertLayerTrace.Error("post-scene-pause", ex)); }
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
            _controller.CoreWebView2.PostWebMessageAsJson(AlertLayerMessages.Hide);
            // D3: in html wallpaper mode the page IS the wallpaper and must stay visible permanently
            // once ready -- End() still tells the page to hide its own alert overlay above, it just
            // never hides the WebView2 layer itself. Video mode is unchanged.
            if (WebViewAlertLayerVisibility.HideOnEndOrDone(_htmlWallpaperMode))
            {
                _controller.IsVisible = false;
            }
        }
        catch (Exception ex) { Debug.WriteLine(ex); _trace?.Invoke(AlertLayerTrace.Error($"end-{reason}", ex)); }
    }

    private void PostShow(AlertShowRequest request)
    {
        if (_controller is null) return;
        try
        {
            _controller.CoreWebView2.PostWebMessageAsJson(AlertLayerMessages.Show(request));
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
            // D3 (html-wallpaper-demo): html wallpaper mode maps and later navigates to the active
            // SCENE page under its OWN reserved example domain, never cosmicwin-alert.example --
            // kept as two separate literal branches, not a shared variable, so video mode's own
            // literals stay byte-for-byte what they were before D3 (see
            // WebViewAlertLayerControllerTests). D6a: the mapping now covers the WHOLE Wallpaper\Web
            // folder (not just one scene's own subfolder), since every scene page now loads
            // Wallpaper\Web\shared\... siblings (the shared alert overlay) -- HtmlWallpaperSceneName
            // is the single place the active scene folder name lives (see its own remarks above).
            if (_htmlWallpaperMode)
            {
                candidate.CoreWebView2.SetVirtualHostNameToFolderMapping("cosmicwin-scene.example",
                    Path.Combine(AppContext.BaseDirectory, "Wallpaper", "Web"),
                    CoreWebView2HostResourceAccessKind.DenyCors);
            }
            else
            {
                candidate.CoreWebView2.SetVirtualHostNameToFolderMapping("cosmicwin-alert.example",
                    Path.Combine(AppContext.BaseDirectory, "Alerts", "Web"), CoreWebView2HostResourceAccessKind.DenyCors);
            }
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
            if (_htmlWallpaperMode)
            {
                // D6d: the scene segment comes from the closed enum -> folder-name mapping (SceneUrl
                // -> SceneFolderName below), and `fps` is a plain integer (30 or 60, from
                // Settings.WallpaperFps) -- neither can ever inject an unexpected path segment or
                // query into this URL. S3: _currentScene, not the constructor's own
                // htmlWallpaperScene parameter -- see that field's own remarks for why.
                _controller.CoreWebView2.Navigate(SceneUrl(_currentScene, _htmlWallpaperFps));
            }
            else
            {
                _controller.CoreWebView2.Navigate("https://cosmicwin-alert.example/alert-layer.html");
            }
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
                // D3: in html wallpaper mode the page's own "done" must never hide the WebView2 layer
                // either -- it is the wallpaper, not a one-off alert. Video mode is unchanged.
                if (WebViewAlertLayerVisibility.HideOnEndOrDone(_htmlWallpaperMode) && _controller is not null)
                {
                    _controller.IsVisible = false;
                }
            }
        }
        catch (Exception ex) { Debug.WriteLine(ex); _trace?.Invoke(AlertLayerTrace.Error("message", ex)); }
    }

    /// <summary>Ready = navigation completed AND the page's own "ready" message received (feature doc). Applies any show that arrived while not yet ready, with its REMAINING duration.</summary>
    private void TryMarkReady()
    {
        if (!_navigationCompleted || !_pageReportedReady) return;
        _state.MarkReady();
        _trace?.Invoke(AlertLayerTrace.PageReady());
        // D3: in html wallpaper mode becoming ready shows the layer on its own, with no Start ever
        // required -- the page IS the wallpaper. Runs again after every recreation (Explorer restart,
        // process failure), so the layer becomes visible again once the new controller is ready.
        // Video mode is unchanged: the layer stays hidden until an alert actually starts it.
        if (WebViewAlertLayerVisibility.ShowOnReady(_htmlWallpaperMode) && _controller is not null)
        {
            _controller.IsVisible = true;
        }
        // A fresh page always starts running: tell it again if the desktop is covered right now.
        if (_htmlWallpaperMode && _scenePaused) PostScenePause();
        if (_state.ApplyPendingShowIfDue() is { } pending)
        {
            _trace?.Invoke(AlertLayerTrace.PendingShowApplied(pending));
            PostShow(pending);
        }
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

    /// <summary>
    /// D6d (html-wallpaper-demo): the ONLY place a <see cref="WallpaperScene"/> value becomes a folder
    /// name -- a closed switch over a compile-time-fixed enum, so no settings text (or anything else)
    /// can ever reach <see cref="CreateAsync"/>'s Navigate URL as an unvalidated scene segment. An
    /// enum value outside the defined members (never produced by <c>Settings.Parse</c>'s own
    /// <c>TryReadWallpaperScene</c>, which only ever returns a defined member) falls back to
    /// <c>"processing"</c>, the same folder <see cref="WallpaperScene.Processing"/> itself maps to --
    /// internal so <c>WebViewAlertLayerControllerTests</c> can exercise this pure mapping directly.
    /// </summary>
    internal static string SceneFolderName(WallpaperScene scene) => scene switch
    {
        WallpaperScene.Explorer => "explorer",
        WallpaperScene.Idle => "idle",
        WallpaperScene.Raphael => "raphael",
        _ => "processing",
    };

    /// <summary>
    /// S3 (wallpaper-scene-http-endpoint): the exact URL <see cref="CreateAsync"/> and <see
    /// cref="SwitchScene"/> both navigate to for <paramref name="scene"/> -- pulled out as a pure,
    /// directly-testable function (unlike the rest of this class's WebView2-only behaviour, see the
    /// class remarks) since both call sites need to build the identical URL. Internal so
    /// <c>WebViewAlertLayerControllerTests</c> can exercise it directly.
    /// </summary>
    internal static string SceneUrl(WallpaperScene scene, int fps, string? variant = null) =>
        $"https://cosmicwin-scene.example/{SceneFolderName(scene)}/index.html?fps={fps}"
        + (variant is null ? "" : $"&variant={variant}");

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
