using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using CosmicWin.Interop.Win32;
using Microsoft.Web.WebView2.Core;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace CosmicWin.App.Alerts;

/// <summary>Single-alert WebView2 composition layer. Construct on the owning STA; start on its pumped dispatcher.</summary>
public sealed class WebViewAlertLayerController : IDisposable
{
    private readonly Win32VideoWallpaperHost _host;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _poll;
    private CoreWebView2Environment? _environment;
    private CoreWebView2CompositionController? _controller;
    private int _generation;
    private nint _hwnd;
    private readonly AlertLayerLifecycle _lifecycle = new();
    private bool _disposed;
    private bool _creating;
    private string? _kind;
    private int _duration;

    public WebViewAlertLayerController(Win32VideoWallpaperHost host)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("A WPF UI STA is required.");
        _host = host;
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
        End();
        _lifecycle.Start(durationMilliseconds);
        _kind = kind;
        _duration = durationMilliseconds;
        _poll.Start();
        Poll();
    }

    public void End()
    {
        CheckAccess();
        _lifecycle.Done();
        _kind = null;
        _poll.Stop();
        Close();
    }

    private void Poll()
    {
        if (_kind is null) return;
        try
        {
            if (!_lifecycle.Poll(_host.Hwnd, _host.CompositionGeneration))
            {
                End();
                return;
            }
            if (!_host.IsCompositionReady || _host.Hwnd == 0)
            {
                Close();
                return;
            }
            if (_controller is not null &&
                (_generation != _host.CompositionGeneration || _hwnd != _host.Hwnd)) Close();
            if (_controller is null && !_creating && _lifecycle.CanCreate)
                _ = CreateAsync(_host.Hwnd, _host.CompositionGeneration);
            else if (_controller is not null) Resize();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            _lifecycle.Failed();
            Close();
        }
    }

    private async Task CreateAsync(nint hwnd, int generation)
    {
        _creating = true;
        CoreWebView2CompositionController? candidate = null;
        bool visualAdded = false;
        var epoch = _lifecycle.Epoch;
        try
        {
            _environment ??= await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CosmicWin", "WebView2Alerts"));
            if (!Current()) return;
            var options = _environment.CreateCoreWebView2ControllerOptions();
            options.DefaultBackgroundColor = Color.Transparent;
            candidate = await _environment.CreateCoreWebView2CompositionControllerAsync(hwnd, options);
            if (!Current()) return;
            candidate.DefaultBackgroundColor = Color.Transparent;
            var visual = _host.AddCompositionOverlayVisual();
            if (visual is null)
            {
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
            _controller = candidate;
            candidate = null;
            visualAdded = false;
            _hwnd = hwnd;
            _generation = generation;
            Resize();
            _controller.IsVisible = true;
            _controller.CoreWebView2.Navigate($"https://cosmicwin-alert.local/alert-layer.html#kind={_kind}&duration={_duration}");
            await _lifecycle.CreateAsync(hwnd, generation, () => Task.FromResult<IDisposable?>(new CallbackDisposable(Close)));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WebView alert creation failed: {ex}");
            _lifecycle.Failed();
            Close();
        }
        finally
        {
            try { candidate?.Close(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
            if (visualAdded)
            {
                try { _host.RemoveCompositionOverlayVisual(); _host.CommitComposition(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
            }
            _creating = false;
            if (_controller is null) _environment = null;
            if (_kind is not null && _controller is null && epoch != _lifecycle.Epoch) Poll();
        }

        bool Current() => epoch == _lifecycle.Epoch && _kind is not null && _lifecycle.Poll(hwnd, generation) && _host.IsCompositionReady &&
            _host.Hwnd == hwnd && _host.CompositionGeneration == generation;
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try { if (args.TryGetWebMessageAsString() == "done") End(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint hwnd, out RECT rect);

    private void Resize()
    {
        if (_controller is not null && GetClientRect(_hwnd, out var rect))
            _controller.Bounds = new Rectangle(0, 0, rect.right - rect.left, rect.bottom - rect.top);
    }

    private void Close()
    {
        var old = _controller;
        _controller = null;
        if (old is null) return;
        _environment = null;
        try { old.CoreWebView2.WebMessageReceived -= OnMessage; } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        try { old.Close(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        try { _host.RemoveCompositionOverlayVisual(); _host.CommitComposition(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }

    private void CheckAccess()
    {
        if (!_dispatcher.CheckAccess()) throw new InvalidOperationException("Use the owning UI dispatcher.");
    }

    public void Dispose()
    {
        CheckAccess();
        if (_disposed) return;
        End();
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
public sealed class AlertLayerLifecycle(Func<DateTimeOffset>? clock = null) : IDisposable
{
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
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
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); Failed(); }
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
