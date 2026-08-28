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
/// <para>
/// AND THE USER'S OWN FINGERS ARE PART OF THE MESSAGE. Sent from a hotkey handler, the chord that
/// asked for this is still physically held — measured: <c>Alt+Shift+Q</c> reached the executor five
/// times in one second, every one traced <c>asked=True</c>, and the desktop count never moved. The
/// shell wants an EXACT modifier set the way <c>RegisterHotKey</c> does, so what it actually
/// received was <c>Alt+Shift+Win+Ctrl+F4</c> and it ignored all five in silence. Whatever is held
/// and does not belong to the chord is released first; see <see cref="BuildChord"/>.
/// </para>
/// </remarks>
internal static class ShellDesktopShortcuts
{
    /// <summary>
    /// The modifiers a <c>Win+Ctrl</c> chord does NOT want, released in this order when held.
    /// </summary>
    /// <remarks>
    /// Both sides of each, because the shell distinguishes them and a user on a Spanish or
    /// US-International layout reaches these chords with the RIGHT Alt. Ctrl and Win are absent on
    /// purpose: they belong to the chord, so holding them is not contamination.
    /// </remarks>
    private static readonly VIRTUAL_KEY[] Contaminants =
        [VIRTUAL_KEY.VK_LMENU, VIRTUAL_KEY.VK_RMENU, VIRTUAL_KEY.VK_LSHIFT, VIRTUAL_KEY.VK_RSHIFT];

    /// <summary><c>Win+Ctrl+D</c>: creates a desktop and switches to it.</summary>
    public static void SendCreateDesktop() => SendWinCtrlChord(VIRTUAL_KEY.VK_D);

    /// <summary><c>Win+Ctrl+F4</c>: closes the current desktop and switches away from it.</summary>
    public static void SendCloseDesktop() => SendWinCtrlChord(VIRTUAL_KEY.VK_F4);

    /// <summary>
    /// The keystrokes to send for <c>Win+Ctrl+</c><paramref name="key"/>, given the modifiers
    /// <paramref name="held"/> currently down.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every release comes BEFORE every press. A single foreign modifier still down when the final
    /// key goes down is the entire defect this exists to fix, so the ordering is the contract here
    /// rather than a detail — and it is what the facts assert.
    /// </para>
    /// <para>
    /// Released, never restored. The user's fingers are still on those keys, so their own key-up is
    /// already coming and reconciles the state; pressing them back down would leave a modifier
    /// latched for good if that release were ever missed. A duplicate key-up costs nothing.
    /// </para>
    /// <para>
    /// A pure function taking the held set rather than reading the keyboard itself, so the sequence
    /// is assertable with no live desktop — the same seam <see cref="INativeVirtualDesktops"/> gives
    /// the positional policy one layer up.
    /// </para>
    /// </remarks>
    internal static List<(VIRTUAL_KEY Key, bool Down)> BuildChord(
        VIRTUAL_KEY key, IReadOnlyCollection<VIRTUAL_KEY> held)
    {
        var steps = new List<(VIRTUAL_KEY Key, bool Down)>(Contaminants.Length + 6);

        foreach (var modifier in Contaminants)
        {
            if (held.Contains(modifier))
            {
                steps.Add((modifier, false));
            }
        }

        // Press outer-to-inner and release inner-to-outer, the order a real keyboard produces.
        // The shell only acts on the chord as a whole, so a stray ordering is silently ignored
        // rather than half-applied. Win and Ctrl are pressed even if the user happens to be holding
        // them: skipping the press would leave the shell waiting for a transition never delivered.
        steps.Add((VIRTUAL_KEY.VK_LWIN, true));
        steps.Add((VIRTUAL_KEY.VK_CONTROL, true));
        steps.Add((key, true));
        steps.Add((key, false));
        steps.Add((VIRTUAL_KEY.VK_CONTROL, false));
        steps.Add((VIRTUAL_KEY.VK_LWIN, false));

        return steps;
    }

    private static void SendWinCtrlChord(VIRTUAL_KEY key)
    {
        var steps = BuildChord(key, Contaminants.Where(IsHeld).ToArray());

        var inputs = new INPUT[steps.Count];
        for (var index = 0; index < steps.Count; index++)
        {
            Set(ref inputs[index], steps[index].Key, steps[index].Down);
        }

        PInvoke.SendInput(inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// Whether <paramref name="key"/> is physically down RIGHT NOW.
    /// </summary>
    /// <remarks>
    /// <c>GetAsyncKeyState</c> rather than <c>GetKeyState</c>: this runs on the hotkey path, not on
    /// a window's message loop, so the per-queue state <c>GetKeyState</c> reports is whatever the
    /// last processed message left behind rather than what the user is holding.
    /// </remarks>
    private static bool IsHeld(VIRTUAL_KEY key) => (PInvoke.GetAsyncKeyState((int)key) & 0x8000) != 0;

    private static void Set(ref INPUT input, VIRTUAL_KEY key, bool down)
    {
        input.type = INPUT_TYPE.INPUT_KEYBOARD;
        input.Anonymous.ki.wVk = key;
        input.Anonymous.ki.dwFlags = down ? 0 : KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP;
    }
}
