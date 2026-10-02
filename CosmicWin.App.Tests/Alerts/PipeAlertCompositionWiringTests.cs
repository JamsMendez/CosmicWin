using CosmicWin.App.Diagnostics;
using CosmicWin.App.Input;
using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.App.Tray;
using CosmicWin.Interop;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App.Tests.Alerts;

/// <summary>
/// Proves <c>AppComposition.Wire</c> starts the named-pipe alert server only when
/// <c>alertsEnabled</c> is on, hands it the <c>HandleAlertCommand</c> closure that answers through the
/// one alert queue, and disposes it on shutdown. The pipe-only cases kept from the retired
/// <c>HttpAlertCompositionWiringTests</c> (the local HTTP server was removed); the real
/// <see cref="Win32.NamedPipeAlertCommandServer"/>'s own behaviour is covered by
/// <c>CosmicWin.Interop.Tests</c>.
/// </summary>
public sealed class PipeAlertCompositionWiringTests
{
    private sealed class NoForeground : IForegroundWindowSource
    {
        public nint GetForegroundHandle() => 0;
    }

    private sealed class NullDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class Scheduler
    {
        public IDisposable Schedule(TimeSpan interval, Action callback) => new NullDisposable();
    }

    private sealed class FakeServer(Func<string, string> handle) : IAlertCommandServer
    {
        public bool Started { get; private set; }
        public bool Disposed { get; private set; }
        public void Start() => Started = true;
        public string Send(string command) => handle(command);
        public void Dispose() => Disposed = true;
    }

    private sealed record Harness(
        AppComposition Composition, FakeServer? Pipe, int FactoryCalls, List<string> Events);

    private static Harness Wire(bool alertsEnabled)
    {
        var primary = new FakeDisplay(
            new IntPtr(1), Rectangle.FromSize(0, 0, 1920, 1080), Rectangle.FromSize(0, 0, 1920, 1080), 1.0, true);
        var registry = new WindowRegistry();
        var treeManager = new TreeManager([primary], primary, registry);
        FakeServer? pipe = null;
        var factoryCalls = 0;
        var events = new List<string>();

        var composition = AppComposition.Wire(
            new FakeWorkspace(), treeManager, registry, new NoForeground(),
            new ExceptionListStore(ExceptionList.Empty),
            focusTrace: new RecordingFocusTrace(),
            disableTaskTrigger: () => { },
            scheduleReconcile: new Scheduler().Schedule,
            hookFactory: writer => new LowLevelKeyboardHook(
                writer, new FakeKeyboardHookPlatform(), TimeSpan.FromSeconds(5), () => 0),
            loadExceptions: () => ExceptionList.Empty,
            shutdown: () => { },
            buildTray: _ => new NullDisposable(),
            importVideoWallpaper: p => p,
            alertsEnabled: alertsEnabled,
            createAlertCommandServer: (_, handle, _) =>
            {
                factoryCalls++;
                return pipe = new FakeServer(handle);
            },
            alertDesktopVisible: () => true,
            startAlertLayer: request => events.Add($"start:{string.Join(",", request.Tiles)}"),
            endAlertLayer: () => events.Add("end"));

        return new Harness(composition, pipe, factoryCalls, events);
    }

    [Fact]
    public void AlertsEnabled_PipeServerIsStarted()
    {
        var h = Wire(alertsEnabled: true);
        using (h.Composition)
        {
            Assert.Equal(1, h.FactoryCalls);
            Assert.True(h.Pipe!.Started);
        }
    }

    [Fact]
    public void AlertsDisabled_PipeServerIsNeverCreated()
    {
        var h = Wire(alertsEnabled: false);
        using (h.Composition)
        {
            Assert.Equal(0, h.FactoryCalls);
            Assert.Null(h.Pipe);
        }
    }

    [Fact]
    public void PipeHandler_AnswersOkAndStartsTheAlertLayer()
    {
        var h = Wire(alertsEnabled: true);
        using (h.Composition)
        {
            var reply = h.Pipe!.Send("warning:1");

            Assert.Equal(AlertPipeProtocol.OkReply, reply);
            Assert.StartsWith("start:warning", Assert.Single(h.Events));
        }
    }

    [Fact]
    public void PipeServerIsDisposedOnShutdown()
    {
        var h = Wire(alertsEnabled: true);
        h.Composition.Dispose();

        Assert.True(h.Pipe!.Disposed);
    }
}
