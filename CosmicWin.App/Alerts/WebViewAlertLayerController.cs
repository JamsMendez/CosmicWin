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
/// Single-alert WebView2 composition layer. Construct on the owning STA; start on its pumped
/// dispatcher.
/// </summary>
/// <remarks>
/// T9a (webview-alert-layer): every lifecycle event is traced through the optional <paramref
/// name="trace"/> delegate (production wires <c>desktopTrace.Record</c>, see
/// <c>AppComposition.WireProduction</c>) -- T6 found production had NO navigation/render telemetry
/// at all, which left F1 (the first alert after launch never showing) and F2 (WebView startup eating
/// the alert's duration) unexplained. <see cref="AlertLayerTrace"/> owns the exact wording and is
/// unit-tested directly; the call sites here that only fire once a real WebView2 environment/
/// controller exists (create start/ready, navigation, process-failed) stay a hardware-only concern
/// like the rest of this class's async creation path -- see T3/T6's progress notes. <see cref="Start"/>/
/// <see cref="End"/> trace "show"/"hide" unconditionally, so those two ARE exercised without a real
/// WebView2 (see <c>WebViewAlertLayerControllerTests</c>).
/// </remarks>
public sealed class WebViewAlertLayerController : IDisposable
{
    private readonly Win32VideoWallpaperHost _host;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _poll;
    private readonly Action<string>? _trace;
    private CoreWebView2Environment? _environment;
    private CoreWebView2CompositionController? _controller;
    private int _generation;
    private nint _hwnd;
    private readonly AlertLayerLifecycle _lifecycle;
    private bool _disposed;
    private bool _creating;
    private string? _kind;
    private int _duration;

    public WebViewAlertLayerController(Win32VideoWallpaperHost host, Action<string>? trace = null)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("A WPF UI STA is required.");
        _host = host;
        _trace = trace;
        _lifecycle = new AlertLayerLifecycle(trace: trace);
        _dispatcher = Dispatcher.CurrentDispatcher;
        _poll = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background,
            (_, _) => Poll(), _dispatcher);
    }

    public void Start(string kind, int durationMilliseconds)
    {
        CheckAccess();
        if (SynchronizationContext.Current is not DispatcherSynchronizationContext)
            throw new InvalidOperationException("A pumped WPF UI STA is required to start the alert layer.");
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (kind is not ("warning" or "failed")) throw new ArgumentOutOfRangeException(nameof(kind));
        if (durationMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(durationMilliseconds));
        End("restart");
        _trace?.Invoke(AlertLayerTrace.Show(kind, durationMilliseconds));
        _lifecycle.Start(durationMilliseconds);
        _kind = kind;
        _duration = durationMilliseconds;
        _poll.Start();
        Poll();
    }

    public void End() => End("end");

    private void End(string reason)
    {
        CheckAccess();
        if (_kind is not null) _trace?.Invoke(AlertLayerTrace.Hide());
        _lifecycle.Done();
        _kind = null;
        _poll.Stop();
        Close(reason);
    }

    private void Poll()
    {
        if (_kind is null) return;
        try
        {
            if (!_lifecycle.Poll(_host.Hwnd, _host.CompositionGeneration))
            {
                End("deadline");
                return;
            }
            if (!_host.IsCompositionReady || _host.Hwnd == 0)
            {
                Close("host-not-ready");
                return;
            }
            if (_controller is not null &&
                (_generation != _host.CompositionGeneration || _hwnd != _host.Hwnd)) Close("host-changed");
            if (_controller is null && !_creating && _lifecycle.CanCreate)
                _ = CreateAsync(_host.Hwnd, _host.CompositionGeneration);
            else if (_controller is not null) Resize();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            _trace?.Invoke(AlertLayerTrace.Error("poll", ex));
            _lifecycle.Failed();
            Close("poll-failed");
        }
    }

    private async Task CreateAsync(nint hwnd, int generation)
    {
        _creating = true;
        var stopwatch = Stopwatch.StartNew();
        _trace?.Invoke(AlertLayerTrace.CreateStart(hwnd, generation));
        CoreWebView2CompositionController? candidate = null;
        bool visualAdded = false;
        var epoch = _lifecycle.Epoch;
        try
        {
            _environment ??= await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CosmicWin", "WebView2Alerts"));
            _trace?.Invoke(AlertLayerTrace.EnvironmentReady(stopwatch.ElapsedMilliseconds));
            if (!Current()) return;
            var options = _environment.CreateCoreWebView2ControllerOptions();
            options.DefaultBackgroundColor = Color.Transparent;
            candidate = await _environment.CreateCoreWebView2CompositionControllerAsync(hwnd, options);
            _trace?.Invoke(AlertLayerTrace.ControllerReady(stopwatch.ElapsedMilliseconds));
            if (!Current()) return;
            candidate.DefaultBackgroundColor = Color.Transparent;
            var visual = _host.AddCompositionOverlayVisual();
            if (visual is null)
            {
                _trace?.Invoke(AlertLayerTrace.NoOverlayVisual());
                await _lifecycle.CreateAsync(hwnd, generation, () => Task.FromResult<IDisposable?>(null));
                return;
            }
            visualAdded = true;
            if (!Current()) return;
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
            _controller.IsVisible = true;
            var navigateStopwatch = Stopwatch.StartNew();
            _navigateStopwatch = navigateStopwatch;
            _controller.CoreWebView2.Navigate($"https://cosmicwin-alert.local/alert-layer.html#kind={_kind}&duration={_duration}");
            await _lifecycle.CreateAsync(hwnd, generation, () => Task.FromResult<IDisposable?>(new CallbackDisposable(() => Close("lifecycle-invalidated"))));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WebView alert creation failed: {ex}");
            _trace?.Invoke(AlertLayerTrace.Error("create", ex));
            _lifecycle.Failed();
            Close("create-failed");
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
            if (_controller is null) _environment = null;
            if (_kind is not null && _controller is null && epoch != _lifecycle.Epoch) Poll();
        }

        bool Current() => epoch == _lifecycle.Epoch && _kind is not null && _lifecycle.Poll(hwnd, generation) && _host.IsCompositionReady &&
            _host.Hwnd == hwnd && _host.CompositionGeneration == generation;
    }

    // Set right before Navigate() so OnNavigationCompleted can report elapsed time since THAT call,
    // not since the whole creation started (environment/controller creation already has its own
    // separately-traced timings above).
    private Stopwatch? _navigateStopwatch;

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        try
        {
            _trace?.Invoke(AlertLayerTrace.NavigationCompleted(
                args.IsSuccess, args.WebErrorStatus, _navigateStopwatch?.ElapsedMilliseconds ?? 0));
        }
        catch (Exception ex) { Debug.WriteLine(ex); _trace?.Invoke(AlertLayerTrace.Error("navigation-completed", ex)); }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs args)
    {
        try
        {
            _trace?.Invoke(AlertLayerTrace.ProcessFailed(args.ProcessFailedKind, args.Reason));
            Close("process-failed");
        }
        catch (Exception ex) { Debug.WriteLine(ex); _trace?.Invoke(AlertLayerTrace.Error("process-failed", ex)); }
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            if (args.TryGetWebMessageAsString() == "done")
            {
                _trace?.Invoke(AlertLayerTrace.Done());
                End("done");
            }
        }
        catch (Exception ex) { Debug.WriteLine(ex); _trace?.Invoke(AlertLayerTrace.Error("message", ex)); }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint hwnd, out RECT rect);

    private void Resize()
    {
        if (_controller is not null && GetClientRect(_hwnd, out var rect))
            _controller.Bounds = new Rectangle(0, 0, rect.right - rect.left, rect.bottom - rect.top);
    }

    private void Close(string reason)
    {
        var old = _controller;
        _controller = null;
        if (old is null) return;
        _trace?.Invoke(AlertLayerTrace.Close(reason));
        _environment = null;
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
        End("disposed");
        _environment = null;
        _lifecycle.Dispose();
        _disposed = true;
    }
}

internal sealed class CallbackDisposable(Action callback) : IDisposable
{
    public void Dispose() => callback();
}

/// <summary>Request deadline, host identity, and async resource ownership shared by the UI controller.</summary>
public sealed class AlertLayerLifecycle(Func<DateTimeOffset>? clock = null, Action<string>? trace = null) : IDisposable
{
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly Action<string>? _trace = trace;
    private IDisposable? _active;
    private DateTimeOffset _deadline;
    private DateTimeOffset _retryAfter;
    private bool _running;
    private bool _disposed;
    private nint _hwnd;
    private int _generation;
    private int _failures;
    public int Epoch { get; private set; }
    public bool IsActive => _active is not null;
    public bool CanCreate => _running && _clock() >= _retryAfter;

    public void Start(int durationMilliseconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Done();
        _running = true;
        _deadline = _clock().AddMilliseconds(Math.Clamp((long)durationMilliseconds + 2000, 2000, 60000));
        _failures = 0;
        _retryAfter = DateTimeOffset.MinValue;
    }

    public bool Poll(nint hwnd, int generation)
    {
        if (!_running) return false;
        if (_clock() >= _deadline) { Done(); return false; }
        if (_hwnd != hwnd || _generation != generation)
        {
            ++Epoch;
            _active?.Dispose();
            _active = null;
            _hwnd = hwnd;
            _generation = generation;
        }
        return true;
    }

    public async Task CreateAsync(nint hwnd, int generation, Func<Task<IDisposable?>> create)
    {
        if (!CanCreate || !Poll(hwnd, generation)) return;
        var epoch = Epoch;
        IDisposable? candidate = null;
        try
        {
            candidate = await create();
            if (_running && epoch == Epoch && Poll(hwnd, generation))
            {
                if (candidate is null) { Failed(); return; }
                _active?.Dispose();
                _active = candidate;
                candidate = null;
                _failures = 0;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            _trace?.Invoke(AlertLayerTrace.Error("lifecycle-create", ex));
            Failed();
        }
        finally { candidate?.Dispose(); }
    }

    public void Failed()
    {
        _retryAfter = _clock().AddMilliseconds(Math.Min(8000, 500 * (1 << Math.Min(_failures++, 4))));
    }

    public void Done()
    {
        ++Epoch;
        _running = false;
        _active?.Dispose();
        _active = null;
    }

    public void Dispose() { if (_disposed) return; Done(); _disposed = true; }
}
