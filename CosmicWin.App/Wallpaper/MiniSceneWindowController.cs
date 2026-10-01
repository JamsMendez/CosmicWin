using CosmicWin.App.Alerts;
using CosmicWin.Interop;
using CosmicWin.Interop.Win32;
using CosmicWin.Layout;
using DrawingRectangle = System.Drawing.Rectangle;

namespace CosmicWin.App.Wallpaper;

/// <summary>
/// The mini scene window (<see cref="WallpaperMode.HtmlMini"/>): a small, square, always-on-top,
/// click-through, see-through window showing the <c>?variant=mini</c> scene page.
/// </summary>
public interface IMiniSceneWindow : IDisposable
{
    /// <summary>
    /// True once the browser is attached and the page can be driven (moved, switched, sent alerts). False
    /// before that, after an attach failure, while a WebView2 process failure is being recovered, and
    /// after dispose. The window can already be on screen (empty) while this is still false.
    /// </summary>
    bool IsReady { get; }

    /// <summary>
    /// Creates and places the window at <paramref name="bounds"/> and starts attaching the scene. False if the
    /// window could not be created or placed; true only says the window exists -- see <see cref="IsReady"/>.
    /// </summary>
    bool Show(WallpaperScene scene, int fps, Rect bounds);

    /// <summary>Switches scene live. The current scene again is an accepted no-op returning true.</summary>
    bool SwitchScene(WallpaperScene scene);

    /// <summary>Moves the window (physical pixels), never activating it.</summary>
    void MoveTo(Rect bounds);

    /// <summary>Forwards an alert to the page (posted as soon as the page is ready).</summary>
    void ShowAlert(AlertShowRequest request);

    /// <summary>Tells the page to hide its alert overlay.</summary>
    void HideAlert();
}

/// <summary>The WebView2 side of the mini window, behind a seam so the controller is testable without one.</summary>
public interface IMiniSceneBrowser : IDisposable
{
    /// <summary>Raised when the current page finished navigating AND reported itself ready.</summary>
    event Action? Ready;

    /// <summary>
    /// Raised after the WebView2 process failed and the browser tore its controller down; the argument is a
    /// trace line naming the failure. The owner decides whether to re-attach.
    /// </summary>
    event Action<string>? Failed;

    /// <summary>Creates a transparent composition controller on <paramref name="surface"/>. False on failure.</summary>
    Task<bool> AttachAsync(ICompositionOverlaySurface surface, DrawingRectangle viewport);

    void Navigate(string url);

    void Resize(DrawingRectangle viewport);

    void PostMessage(string json);
}

/// <summary>
/// Owns the mini window's surface and browser. All members run on the owning UI thread. Placement is
/// never activating (see <see cref="MiniSceneWindowStyles"/>); the page viewport is the window's
/// physical pixel size, square.
/// </summary>
public sealed class MiniSceneWindowController : IMiniSceneWindow
{
    private readonly Func<IMiniSceneSurface> _surfaceFactory;
    private readonly IMiniSceneBrowser _browser;
    private readonly Action<string>? _trace;
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private IMiniSceneSurface? _surface;
    private WallpaperScene _scene;
    private int _fps;
    private DrawingRectangle _viewport;
    // At most this many re-attaches after WebView2 process failures for the window's lifetime: a
    // browser that keeps dying gives up (traced) instead of looping.
    private const int MaxRecoveries = 2;
    private int _recoveries;
    private bool _attached;
    // The PAGE reported ready (after each navigation). Not IsReady: that one means the browser is attached.
    private bool _pageReady;
    private string? _pendingAlert;
    private bool _disposed;

    public MiniSceneWindowController(
        Func<IMiniSceneSurface> surfaceFactory, IMiniSceneBrowser browser, Action<string>? trace = null)
    {
        _surfaceFactory = surfaceFactory;
        _browser = browser;
        _trace = trace;
        _browser.Ready += OnReady;
        _browser.Failed += OnFailed;
    }

    public bool IsReady => _attached && !_disposed;

    /// <summary>The real window and WebView2 (its own <c>WebView2Mini</c> user-data folder). Construct on the UI STA.</summary>
    public static MiniSceneWindowController CreateProduction(Action<string>? trace = null) =>
        new(() => new Win32MiniSceneWindow(), new WebView2MiniSceneBrowser(), trace);

    /// <summary>
    /// The page viewport for a window of <paramref name="bounds"/>: origin at zero and SQUARE (the
    /// shorter side), in physical pixels. Rasterization scale is pinned to 1 by the browser, so CSS
    /// pixels equal these physical pixels at any DPI.
    /// </summary>
    public static DrawingRectangle ViewportFor(Rect bounds)
    {
        var side = Math.Max(0, Math.Min(bounds.Width, bounds.Height));
        return new DrawingRectangle(0, 0, side, side);
    }

    public bool Show(WallpaperScene scene, int fps, Rect bounds)
    {
        CheckAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_surface is not null)
        {
            SwitchScene(scene);
            MoveTo(bounds);
            return true;
        }

        _scene = scene;
        _fps = fps;
        _viewport = ViewportFor(bounds);
        var surface = _surfaceFactory();
        if (!surface.TryCreate(ToInterop(bounds)))
        {
            _trace?.Invoke("mini-window: create failed");
            surface.Dispose();
            return false;
        }

        _surface = surface;
        // The window is created hidden; Place is what shows it (topmost, no activation).
        if (!surface.Place(ToInterop(bounds)))
        {
            _trace?.Invoke("mini-window: place failed");
            _surface = null;
            surface.Dispose();
            return false;
        }

        _ = AttachAsync(surface);
        return true;
    }

    public bool SwitchScene(WallpaperScene scene)
    {
        CheckAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_scene == scene) return true;
        _scene = scene;
        // Before the browser is attached only the scene is recorded: the first navigation reads it.
        if (_attached) NavigateToScene();
        return true;
    }

    public void MoveTo(Rect bounds)
    {
        CheckAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        _surface?.Place(ToInterop(bounds));
        var viewport = ViewportFor(bounds);
        if (viewport == _viewport) return;
        _viewport = viewport;
        if (_attached) _browser.Resize(viewport);
    }

    public void ShowAlert(AlertShowRequest request)
    {
        CheckAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        // The window is the whole canvas: lay the mosaic out on all of it, not on a monitor work area.
        Post(AlertLayerMessages.Show(request with
        {
            WorkAreaLeft = 0, WorkAreaTop = 0, WorkAreaWidth = 0, WorkAreaHeight = 0,
        }));
    }

    public void HideAlert()
    {
        CheckAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        _pendingAlert = null;
        if (_pageReady) _browser.PostMessage(AlertLayerMessages.Hide);
    }

    public void Dispose()
    {
        CheckAccess();
        if (_disposed) return;
        _disposed = true;
        _browser.Ready -= OnReady;
        _browser.Failed -= OnFailed;
        _browser.Dispose();
        _surface?.Dispose();
        _surface = null;
    }

    private async Task AttachAsync(IMiniSceneSurface surface)
    {
        bool attached;
        try
        {
            attached = await _browser.AttachAsync(surface, _viewport);
        }
        catch (Exception ex)
        {
            _trace?.Invoke($"mini-window: attach failed: {ex.Message}");
            return;
        }

        if (_disposed) return;
        if (!attached)
        {
            _trace?.Invoke("mini-window: attach failed");
            return;
        }

        _attached = true;
        NavigateToScene();
    }

    private void OnFailed(string reason)
    {
        if (_disposed) return;
        _attached = false;
        _pageReady = false;
        _trace?.Invoke($"mini-window: {reason}");
        if (_surface is not { } surface) return;
        if (_recoveries >= MaxRecoveries)
        {
            _trace?.Invoke("mini-window: recovery exhausted, staying down");
            return;
        }

        _recoveries++;
        _trace?.Invoke($"mini-window: recovering attempt={_recoveries}");
        _ = AttachAsync(surface);
    }

    private void NavigateToScene()
    {
        _pageReady = false;
        _browser.Navigate(WebViewAlertLayerController.SceneUrl(_scene, _fps, "mini"));
    }

    private void Post(string json)
    {
        if (_pageReady) _browser.PostMessage(json);
        else _pendingAlert = json;
    }

    private void OnReady()
    {
        _pageReady = true;
        if (_pendingAlert is not { } pending) return;
        _pendingAlert = null;
        _browser.PostMessage(pending);
    }

    private static Rectangle ToInterop(Rect bounds) =>
        Rectangle.FromSize(bounds.X, bounds.Y, bounds.Width, bounds.Height);

    private void CheckAccess()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
            throw new InvalidOperationException("Use the owning UI thread.");
    }
}
