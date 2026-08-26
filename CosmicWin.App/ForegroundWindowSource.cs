using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CosmicWin.App;

/// <summary>
/// Resolves the current OS foreground window handle. Owned by the App layer, unlike <see
/// cref="Interop.IWindow.TryActivate"/> (which Interop owns per design) — this is a single
/// branchless native read with no window-tracking state, matching the precedent of App's own
/// direct CsWin32 usage for the keyboard hook (<c>WindowsKeyboardHookPlatform</c>).
/// </summary>
public interface IForegroundWindowSource
{
    /// <summary>The current foreground window handle, or <see cref="IntPtr.Zero"/> if none.</summary>
    nint GetForegroundHandle();

    /// <summary>
    /// What the UI THREAD that owns <paramref name="hwnd"/> believes is active, which is not
    /// necessarily what the system believes. Zero when it cannot be read, or when this source has
    /// nothing to say.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Windows keeps activation state per input queue as well as globally: <c>GetForegroundWindow</c>
    /// answers for the system, <c>GetGUIThreadInfo</c> answers for one thread. They normally agree,
    /// and the interesting case is precisely when they do not — a thread that was attached to
    /// another process's input queue and then detached is not reliably told it lost the foreground,
    /// and an application that paints its own non-client frame from thread-local state will keep
    /// drawing itself active while the system has moved on.
    /// </para>
    /// <para>
    /// A default of zero rather than a required member: the dozen-odd fakes that only ever needed a
    /// foreground handle have nothing truthful to say here, and inventing an answer for them would
    /// put fiction on a diagnostic line whose whole value is that it is measured.
    /// </para>
    /// </remarks>
    nint GetActiveWindowOfThreadOwning(nint hwnd) => 0;
}

internal sealed class Win32ForegroundWindowSource : IForegroundWindowSource
{
    public nint GetForegroundHandle() => PInvoke.GetForegroundWindow();

    /// <summary>
    /// Reads the owning thread's own activation state. Never throws and never blocks: a thread that
    /// has no GUI state, or one this process may not query, simply answers zero.
    /// </summary>
    public unsafe nint GetActiveWindowOfThreadOwning(nint hwnd)
    {
        if (hwnd == 0)
        {
            return 0;
        }

        uint threadId = PInvoke.GetWindowThreadProcessId(new HWND(hwnd), null);
        if (threadId == 0)
        {
            return 0;
        }

        // cbSize must be set before the call; GetGUIThreadInfo rejects the struct otherwise.
        var info = new GUITHREADINFO { cbSize = (uint)sizeof(GUITHREADINFO) };
        return PInvoke.GetGUIThreadInfo(threadId, ref info) ? info.hwndActive : 0;
    }
}
