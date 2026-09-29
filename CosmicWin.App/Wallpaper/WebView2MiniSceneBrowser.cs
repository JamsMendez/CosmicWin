using System.Diagnostics;
using System.Drawing;
using DrawingRectangle = System.Drawing.Rectangle;
using System.IO;
using CosmicWin.App.Alerts;
using CosmicWin.Interop;
using Microsoft.Web.WebView2.Core;

namespace CosmicWin.App.Wallpaper;

/// <summary>
/// The real <see cref="IMiniSceneBrowser"/>: a transparent WebView2 composition controller rendered
/// into the overlay visual of an <see cref="ICompositionOverlaySurface"/>, the same path
/// <c>WebViewAlertLayerController</c> uses for the html wallpaper. Own user-data folder
/// (<c>WebView2Mini</c>) so it never contends with the alert layer's browser process. Hardware-only,
/// like the alert layer's WebView2 shell; the logic around it is tested through the seam.
/// </summary>
public sealed class WebView2MiniSceneBrowser : IMiniSceneBrowser
{
    private ICompositionOverlaySurface? _surface;
    private CoreWebView2Environment? _environment;
    private CoreWebView2CompositionController? _controller;
    private bool _navigationCompleted;
    private bool _pageReportedReady;
    private bool _disposed;

    public event Action? Ready;
    public event Action<string>? Failed;

    public WebView2MiniSceneBrowser()
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("A WPF UI STA is required.");
    }

    public async Task<bool> AttachAsync(ICompositionOverlaySurface surface, DrawingRectangle viewport)
    {
        // Everything half-built is released again unless the attach runs all the way through.
        CoreWebView2CompositionController? candidate = null;
        var visualAdded = false;
        var attached = false;
        try
        {
            _environment ??= await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CosmicWin", "WebView2Mini"));
            if (_disposed) return false;
            var options = _environment.CreateCoreWebView2ControllerOptions();
            options.DefaultBackgroundColor = Color.Transparent;
            candidate = await _environment.CreateCoreWebView2CompositionControllerAsync(surface.Hwnd, options);
            if (_disposed) return false;

            candidate.DefaultBackgroundColor = Color.Transparent;
            var visual = surface.AddCompositionOverlayVisual();
            if (visual is null) return false;
            visualAdded = true;

            candidate.RootVisualTarget = visual;
            surface.CommitComposition();
            // CSS pixels == physical pixels: the mini scenes are drawn for a fixed-size square regardless
            // of the monitor's DPI scale, so the controller must not apply its own scale factor.
            candidate.ShouldDetectMonitorScaleChanges = false;
            candidate.RasterizationScale = 1.0;
            candidate.Bounds = viewport;
            candidate.CoreWebView2.SetVirtualHostNameToFolderMapping("cosmicwin-scene.example",
                Path.Combine(AppContext.BaseDirectory, "Wallpaper", "Web"), CoreWebView2HostResourceAccessKind.DenyCors);
            candidate.CoreWebView2.WebMessageReceived += OnMessage;
            candidate.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            candidate.CoreWebView2.ProcessFailed += OnProcessFailed;
            candidate.IsVisible = true;
            _surface = surface;
            _controller = candidate;
            attached = true;
            return true;
        }
        finally
        {
            if (!attached)
            {
                if (candidate is not null)
                {
                    try { candidate.Close(); }
                    catch (Exception ex) { Debug.WriteLine(ex); }
                }

                if (visualAdded) surface.RemoveCompositionOverlayVisual();
                // An environment that failed to produce a controller may be poisoned (its browser
                // process gone): the next attach builds a fresh one. It has no Dispose of its own.
                if (!_disposed) _environment = null;
            }
        }
    }

    public void Navigate(string url)
    {
        if (_controller is null) return;
        _navigationCompleted = false;
        _pageReportedReady = false;
        _controller.CoreWebView2.Navigate(url);
    }

    public void Resize(DrawingRectangle viewport)
    {
        if (_controller is not null) _controller.Bounds = viewport;
    }

    public void PostMessage(string json)
    {
        try { _controller?.CoreWebView2.PostWebMessageAsJson(json); }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs args)
    {
        try
        {
            var reason = AlertLayerTrace.ProcessFailed(args.ProcessFailedKind, args.Reason);
            // The failed browser and everything hanging off it is dropped; the owner re-attaches.
            ReleaseController();
            _environment = null;
            Failed?.Invoke(reason);
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (!args.IsSuccess) return;
        _navigationCompleted = true;
        TryRaiseReady();
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            if (args.TryGetWebMessageAsString() != "ready") return;
            _pageReportedReady = true;
            TryRaiseReady();
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    private void TryRaiseReady()
    {
        if (_navigationCompleted && _pageReportedReady) Ready?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseController();
    }

    private void ReleaseController()
    {
        var controller = _controller;
        _controller = null;
        _navigationCompleted = false;
        _pageReportedReady = false;
        if (controller is not null)
        {
            try { controller.CoreWebView2.WebMessageReceived -= OnMessage; }
            catch (Exception ex) { Debug.WriteLine(ex); }
            try { controller.CoreWebView2.NavigationCompleted -= OnNavigationCompleted; }
            catch (Exception ex) { Debug.WriteLine(ex); }
            try { controller.CoreWebView2.ProcessFailed -= OnProcessFailed; }
            catch (Exception ex) { Debug.WriteLine(ex); }
            try { controller.Close(); }
            catch (Exception ex) { Debug.WriteLine(ex); }
        }

        _surface?.RemoveCompositionOverlayVisual();
        _surface = null;
    }
}
