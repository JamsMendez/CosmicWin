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
