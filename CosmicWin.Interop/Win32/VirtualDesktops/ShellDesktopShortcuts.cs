using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace CosmicWin.Interop.Win32.VirtualDesktops;

/// <summary>
/// Drives Windows' own virtual-desktop shortcuts through <c>SendInput</c>.
/// </summary>
/// <remarks>
/// <para>
/// These are the DOCUMENTED, user-facing shortcuts the shell has honoured for a decade, so unlike
/// <see cref="IVirtualDesktopManagerInternal"/> they cannot silently change meaning under a Windows
/// update. They exist here for two reasons: they are the fallback if the internal vtable ever stops
/// matching, and they are how a test arranges MORE THAN ONE desktop without calling an unverified
/// mutator — which is exactly the thing the probe exists to prevent.
/// </para>
/// <para>
/// Synthetic input is never free: this is real keyboard traffic on the user's desktop, and the
/// shell switches to the new desktop as a side effect. Same reasoning as the Alt taps in
/// <see cref="Win32NativeWindowSource"/> — legitimate, but never the first choice.
/// </para>
/// </remarks>
internal static class ShellDesktopShortcuts
{
    /// <summary><c>Win+Ctrl+D</c>: creates a desktop and switches to it.</summary>
    public static void SendCreateDesktop() => SendWinCtrlChord(VIRTUAL_KEY.VK_D);

    /// <summary><c>Win+Ctrl+F4</c>: closes the current desktop and switches away from it.</summary>
    public static void SendCloseDesktop() => SendWinCtrlChord(VIRTUAL_KEY.VK_F4);

    private static void SendWinCtrlChord(VIRTUAL_KEY key)
    {
        // Press outer-to-inner and release inner-to-outer, the order a real keyboard produces.
        // The shell only acts on the chord as a whole, so a stray ordering is silently ignored
        // rather than half-applied.
        var inputs = new INPUT[6];
        Set(ref inputs[0], VIRTUAL_KEY.VK_LWIN, down: true);
        Set(ref inputs[1], VIRTUAL_KEY.VK_CONTROL, down: true);
        Set(ref inputs[2], key, down: true);
        Set(ref inputs[3], key, down: false);
        Set(ref inputs[4], VIRTUAL_KEY.VK_CONTROL, down: false);
        Set(ref inputs[5], VIRTUAL_KEY.VK_LWIN, down: false);

        PInvoke.SendInput(inputs, Marshal.SizeOf<INPUT>());
    }

    private static void Set(ref INPUT input, VIRTUAL_KEY key, bool down)
    {
        input.type = INPUT_TYPE.INPUT_KEYBOARD;
        input.Anonymous.ki.wVk = key;
        input.Anonymous.ki.dwFlags = down ? 0 : KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP;
    }
}
