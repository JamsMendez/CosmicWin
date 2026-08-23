using System.Diagnostics;
using CosmicWin.Interop.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// The "new window is not tiled" defect. <c>SubscribeWindowEvents</c>
/// subscribed the whole <c>EVENT_OBJECT_CREATE</c>..<c>EVENT_OBJECT_LOCATIONCHANGE</c> range but
/// handled only CREATE, DESTROY and LOCATIONCHANGE — <c>EVENT_OBJECT_SHOW</c> was already arriving
/// with no case for it. Because trackability requires <c>IsWindowVisible</c>, and a top-level window
/// is normally created hidden and shown a moment later, the CREATE callback dropped such a window
/// and nothing ever looked again: <c>Win32Workspace.Poll()</c> is not called on any production path.
/// Restarting CosmicWin tiled everything only because <c>Open()</c> enumerates a fresh snapshot.
/// </summary>
/// <remarks>
/// <c>WINEVENT_OUTOFCONTEXT</c> hooks deliver their callbacks while the INSTALLING thread retrieves
/// messages, so this fixture must pump its own queue. Production gets that for free — the hook is
/// installed from <c>App.OnStartup</c> on the WPF UI thread, which already runs a message loop — but
/// an xunit thread has no pump, and without one the callback never fires no matter what the switch
/// handles.
/// </remarks>
[Trait("Category", "RequiresDesktop")]
[Collection(RealDesktopCollection.Name)]
public sealed class Win32NativeWindowSourceShowEventTests
{
    [RequiresDesktopFact]
    public void SubscribeWindowEvents_ReportsCreated_ForAWindowThatIsOnlyVisibleAfterItsCreateEvent()
    {
        var source = new Win32NativeWindowSource();
        var created = new HashSet<nint>();

        using var subscription = source.SubscribeWindowEvents((kind, hwnd) =>
        {
            if (kind == NativeWindowEventKind.Created)
            {
                created.Add(hwnd);
            }
        });

        // The spawn MUST run on another thread while this one pumps continuously. Pumping only
        // after the window is already up would evaluate IsTrackable late -- when the window is
        // visible -- and the CREATE arm would pass on its own, hiding the very race this pins.
        SpawnedAlacrittyWindow? window = null;
        var spawn = Task.Run(() => Volatile.Write(ref window, SpawnedAlacrittyWindow.Spawn()));

        try
        {
            var observed = PumpUntil(
                () => Volatile.Read(ref window) is { } spawned && created.Contains(spawned.Handle),
                TimeSpan.FromSeconds(15));

            Assert.True(observed, "No Created event ever reported the spawned window.");
        }
        finally
        {
            spawn.Wait(TimeSpan.FromSeconds(15));
            Volatile.Read(ref window)?.Dispose();
        }
    }

    /// <summary>
    /// Drains this thread's message queue — which is what actually invokes a WINEVENT_OUTOFCONTEXT
    /// callback — until <paramref name="condition"/> holds or the budget runs out.
    /// </summary>
    private static bool PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            while (PInvoke.PeekMessage(out var message, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE))
            {
                PInvoke.TranslateMessage(message);
                PInvoke.DispatchMessage(message);
            }

            if (condition())
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return condition();
    }
}
