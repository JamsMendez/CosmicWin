using System.Diagnostics;
using System.Drawing;
using DrawingRectangle = System.Drawing.Rectangle;
using System.IO;
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

    public WebView2MiniSceneBrowser()
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("A WPF UI STA is required.");
    }

    public async Task<bool> AttachAsync(ICompositionOverlaySurface surface, DrawingRectangle viewport)
    {
        _environment ??= await CoreWebView2Environment.CreateAsync(
            userDataFolder: Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CosmicWin", "WebView2Mini"));
        if (_disposed) return false;
        var options = _environment.CreateCoreWebView2ControllerOptions();
        options.DefaultBackgroundColor = Color.Transparent;
        var candidate = await _environment.CreateCoreWebView2CompositionControllerAsync(surface.Hwnd, options);
        if (_disposed)
        {
            candidate.Close();
            return false;
        }

        candidate.DefaultBackgroundColor = Color.Transparent;
        var visual = surface.AddCompositionOverlayVisual();
        if (visual is null)
        {
            candidate.Close();
            return false;
        }

        candidate.RootVisualTarget = visual;
        surface.CommitComposition();
        // CSS pixels == physical pixels: the mini scenes are drawn for a 288 px square regardless of
        // the monitor's DPI scale, so the controller must not apply its own scale factor.
        candidate.ShouldDetectMonitorScaleChanges = false;
        candidate.RasterizationScale = 1.0;
        candidate.Bounds = viewport;
        candidate.CoreWebView2.SetVirtualHostNameToFolderMapping("cosmicwin-scene.example",
            Path.Combine(AppContext.BaseDirectory, "Wallpaper", "Web"), CoreWebView2HostResourceAccessKind.DenyCors);
        candidate.CoreWebView2.WebMessageReceived += OnMessage;
        candidate.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
        candidate.IsVisible = true;
        _surface = surface;
        _controller = candidate;
        return true;
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
        var controller = _controller;
        _controller = null;
        if (controller is not null)
        {
            try { controller.CoreWebView2.WebMessageReceived -= OnMessage; }
            catch (Exception ex) { Debug.WriteLine(ex); }
            try { controller.CoreWebView2.NavigationCompleted -= OnNavigationCompleted; }
            catch (Exception ex) { Debug.WriteLine(ex); }
            try { controller.Close(); }
            catch (Exception ex) { Debug.WriteLine(ex); }
        }

        _surface?.RemoveCompositionOverlayVisual();
        _surface = null;
    }
}
