using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// Drains the calling thread's message queue, which is what actually invokes a
/// <c>WINEVENT_OUTOFCONTEXT</c> callback.
/// </summary>
/// <remarks>
/// Production gets this for free -- the hooks are installed from <c>App.OnStartup</c> on the WPF UI
/// thread, which already runs a message loop -- but an xunit thread has none, and without a pump
/// the callback never fires no matter what the switch handles. Extracted at the third copy: two
/// test classes already carried an identical private one, and a fourth was about to be written.
/// </remarks>
internal static class MessagePump
{
    /// <summary>Pumps until <paramref name="condition"/> holds or the budget runs out.</summary>
    public static bool Until(Func<bool> condition, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            Drain();

            if (condition())
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return condition();
    }

    /// <summary>Pumps for the whole duration, running <paramref name="each"/> between drains.</summary>
    public static void For(TimeSpan duration, Action? each = null)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < duration)
        {
            Drain();
            each?.Invoke();
            Thread.Sleep(15);
        }
    }

    private static void Drain()
    {
        while (PInvoke.PeekMessage(out var message, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE))
        {
            PInvoke.TranslateMessage(message);
            PInvoke.DispatchMessage(message);
        }
    }
}
