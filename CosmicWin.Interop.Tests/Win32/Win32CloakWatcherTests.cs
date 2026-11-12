using CosmicWin.Interop.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// Reported with tiling off: switching virtual desktops showed the focus border of the window being
/// LEFT on the desktop being entered, for an instant. The overlay is a tool window, which the shell
/// does not cloak on a switch, so it stayed on screen until the next 400 ms tick moved it.
/// </summary>
/// <remarks>
/// The cloak is produced the deterministic way, on a window this test owns, with
/// <c>DWMWA_CLOAK</c> -- the same <c>EVENT_OBJECT_CLOAKED</c> the shell raises for every window on a
/// desktop the user leaves, without switching the maintainer's desktop out from under them.
/// </remarks>
[Trait("Category", "RequiresDesktop")]
[Collection(RealDesktopCollection.Name)]
public sealed class Win32CloakWatcherTests
{
    [RequiresDesktopSessionFact]
    public void Watch_ReportsTheWindow_WhenItIsCloaked()
    {
        var window = CreateProbeWindow();
        try
        {
            var cloaked = new List<nint>();
            using var watcher = Win32CloakWatcher.Watch(cloaked.Add);
            Assert.NotNull(watcher);

            Assert.False(Win32OverlayWindow.IsCloaked(window), "The probe was cloaked before the test cloaked it.");

            Cloak(window, true);

            var observed = MessagePump.Until(() => cloaked.Contains(window), TimeSpan.FromSeconds(5));

            Assert.True(observed, "Cloaking a window raised nothing, so the border would wait for the tick.");
            Assert.True(Win32OverlayWindow.IsCloaked(window));
        }
        finally
        {
            PInvoke.DestroyWindow(new HWND(window));
        }
    }

    [RequiresDesktopSessionFact]
    public void Watch_ReportsNothing_AfterItIsDisposed()
    {
        var window = CreateProbeWindow();
        try
        {
            var cloaked = new List<nint>();
            var watcher = Win32CloakWatcher.Watch(cloaked.Add);
            Assert.NotNull(watcher);
            watcher.Dispose();

            Cloak(window, true);
            MessagePump.For(TimeSpan.FromMilliseconds(500));

            Assert.Empty(cloaked);
        }
        finally
        {
            PInvoke.DestroyWindow(new HWND(window));
        }
    }

    private static unsafe nint CreateProbeWindow()
    {
        // A tool window, so a window manager that happens to be running leaves it alone.
        var hwnd = PInvoke.CreateWindowEx(
            WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE,
            "Static",
            "CosmicWin cloak probe",
            WINDOW_STYLE.WS_POPUP | WINDOW_STYLE.WS_VISIBLE,
            -200, -200, 50, 50,
            HWND.Null,
            null,
            null,
            null);
        Assert.False(hwnd.IsNull, "Could not create the probe window.");
        MessagePump.For(TimeSpan.FromMilliseconds(100));
        return hwnd;
    }

    private static unsafe void Cloak(nint hwnd, bool cloak)
    {
        var value = cloak ? 1 : 0;
        var hr = PInvoke.DwmSetWindowAttribute(
            new HWND(hwnd), DWMWINDOWATTRIBUTE.DWMWA_CLOAK, &value, (uint)sizeof(int));
        Assert.True(hr.Succeeded, $"DWMWA_CLOAK was refused: 0x{hr.Value:X8}");
    }
}
