using CosmicWin.Interop.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// The "a closed Discord keeps its tile" defect, proven against a REAL window rather than a fake
/// that was told to say yes. <c>SubscribeWindowEvents</c> subscribes the whole
/// <c>EVENT_OBJECT_CREATE</c>..<c>EVENT_OBJECT_LOCATIONCHANGE</c> range, and
/// <c>EVENT_OBJECT_HIDE</c> (0x8003) sits inside it -- but had no case, exactly as
/// <c>EVENT_OBJECT_SHOW</c> did before <see cref="Win32NativeWindowSourceShowEventTests"/>.
/// <para>
/// An application that lives in the notification area is closed by HIDING its window, never by
/// destroying it, so nothing this class listens to ever fired and the window kept its slot and its
/// focus border for the rest of the session. Notepad stands in for Discord here: a real top-level
/// window in another process, hidden the same way, through the same API.
/// </para>
/// </summary>
/// <remarks>
/// <c>WINEVENT_OUTOFCONTEXT</c> hooks deliver their callbacks while the INSTALLING thread retrieves
/// messages, so this fixture pumps its own queue -- production gets that for free from the WPF UI
/// thread the hook is installed on, but an xunit thread has none.
/// </remarks>
[Trait("Category", "RequiresDesktop")]
[Collection(RealDesktopCollection.Name)]
public sealed class Win32NativeWindowSourceHideEventTests
{
    [RequiresDesktopSessionFact]
    public void SubscribeWindowEvents_ReportsHidden_ForAWindowThatIsHiddenWithoutBeingDestroyed()
    {
        var source = new Win32NativeWindowSource();
        var hidden = new HashSet<nint>();
        var destroyed = new HashSet<nint>();

        using var subscription = source.SubscribeWindowEvents((kind, hwnd) =>
        {
            switch (kind)
            {
                case NativeWindowEventKind.Hidden:
                    hidden.Add(hwnd);
                    break;
                case NativeWindowEventKind.Destroyed:
                    destroyed.Add(hwnd);
                    break;
            }
        });

        using var window = SpawnedNotepadWindow.Spawn();
        MessagePump.Until(() => false, TimeSpan.FromMilliseconds(200));

        PInvoke.ShowWindow(new HWND(window.Handle), SHOW_WINDOW_CMD.SW_HIDE);

        var observed = MessagePump.Until(() => hidden.Contains(window.Handle), TimeSpan.FromSeconds(10));

        // Restored before the assertions so a failure never leaves an invisible Notepad behind for
        // the next test in this collection to trip over.
        PInvoke.ShowWindow(new HWND(window.Handle), SHOW_WINDOW_CMD.SW_SHOW);

        Assert.True(observed, "No Hidden event ever reported the window that was hidden.");

        // The other half of the contract, and the reason Hidden is its own kind: the window is
        // still ALIVE. Reporting it as destroyed would be a lie the consumer cannot see through.
        Assert.DoesNotContain(window.Handle, destroyed);
        Assert.True(PInvoke.IsWindow(new HWND(window.Handle)));
    }
}
