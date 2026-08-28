using CosmicWin.Interop.Win32.VirtualDesktops;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// The keystroke sequence a shell shortcut is sent as, when the user is STILL HOLDING the chord
/// that asked for it.
/// </summary>
/// <remarks>
/// <para>
/// Measured on hardware, and the reason this file exists: <c>Alt+Shift+Q</c> reached the executor
/// every time -- five presses in one second, all traced <c>CloseDesktop asked=True</c> -- and the
/// desktop count never moved off 4. The chord was fine; the injected <c>Win+Ctrl+F4</c> was not.
/// </para>
/// <para>
/// The shell's virtual-desktop shortcuts want an EXACT modifier set, the way <c>RegisterHotKey</c>
/// does. A hotkey handler runs while the user's fingers are still on Alt and Shift, so what reached
/// the shell was <c>Alt+Shift+Win+Ctrl+F4</c> -- a chord it has never heard of, ignored in silence.
/// The desktop chords that DO work (<c>Alt+1</c>..<c>Alt+9</c>) go through COM and never touch the
/// keyboard, which is why only this one was affected.
/// </para>
/// <para>
/// The sequence is built as a pure function so this can be asserted with no live desktop at all. The
/// <c>SendInput</c> call around it stays as thin as every other interop call here.
/// </para>
/// </remarks>
public sealed class ShellDesktopShortcutSequenceTests
{
    private static readonly VIRTUAL_KEY[] NothingHeld = [];

    /// <summary>With no fingers on the keyboard, nothing is released and the chord is just itself.</summary>
    [Fact]
    public void WithNoModifiersHeld_TheSequenceIsTheChordAlone()
    {
        var sequence = ShellDesktopShortcuts.BuildChord(VIRTUAL_KEY.VK_F4, NothingHeld);

        Assert.Equal(
            [
                (VIRTUAL_KEY.VK_LWIN, true),
                (VIRTUAL_KEY.VK_CONTROL, true),
                (VIRTUAL_KEY.VK_F4, true),
                (VIRTUAL_KEY.VK_F4, false),
                (VIRTUAL_KEY.VK_CONTROL, false),
                (VIRTUAL_KEY.VK_LWIN, false),
            ],
            sequence);
    }

    /// <summary>
    /// The whole fix: a held modifier is released BEFORE the chord starts, so the shell sees the
    /// exact set it is listening for.
    /// </summary>
    [Fact]
    public void AHeldModifier_IsReleasedBeforeTheChordBegins()
    {
        var sequence = ShellDesktopShortcuts.BuildChord(
            VIRTUAL_KEY.VK_F4, [VIRTUAL_KEY.VK_LMENU, VIRTUAL_KEY.VK_LSHIFT]);

        Assert.Equal(
            [
                (VIRTUAL_KEY.VK_LMENU, false),
                (VIRTUAL_KEY.VK_LSHIFT, false),
                (VIRTUAL_KEY.VK_LWIN, true),
                (VIRTUAL_KEY.VK_CONTROL, true),
                (VIRTUAL_KEY.VK_F4, true),
                (VIRTUAL_KEY.VK_F4, false),
                (VIRTUAL_KEY.VK_CONTROL, false),
                (VIRTUAL_KEY.VK_LWIN, false),
            ],
            sequence);
    }

    /// <summary>
    /// Every release comes first, none of them interleaved. A single modifier still down when
    /// <c>F4</c> goes down is the whole defect, so the order is the contract here, not a detail.
    /// </summary>
    [Fact]
    public void EveryReleasePrecedesEveryPress()
    {
        var sequence = ShellDesktopShortcuts.BuildChord(
            VIRTUAL_KEY.VK_D, [VIRTUAL_KEY.VK_RMENU, VIRTUAL_KEY.VK_LSHIFT, VIRTUAL_KEY.VK_RSHIFT]);

        var firstPress = sequence.FindIndex(step => step.Down);
        var lastRelease = sequence.FindLastIndex(step => !step.Down && step.Key != VIRTUAL_KEY.VK_D);

        Assert.Equal(3, firstPress);
        Assert.True(lastRelease > firstPress, "the chord's own key-ups are what should come last");
        Assert.All(sequence.Take(firstPress), step => Assert.False(step.Down));
    }

    /// <summary>
    /// NOT restored afterwards, and deliberately. The user's fingers are still on those keys, so
    /// their own key-up is coming and reconciles the state; pressing them back down would leave a
    /// modifier latched if that release were ever missed. A duplicate key-up costs nothing.
    /// </summary>
    [Fact]
    public void AReleasedModifierIsNotPressedBackDown()
    {
        var sequence = ShellDesktopShortcuts.BuildChord(VIRTUAL_KEY.VK_F4, [VIRTUAL_KEY.VK_LMENU]);

        Assert.DoesNotContain((VIRTUAL_KEY.VK_LMENU, true), sequence);
    }

    /// <summary>
    /// The chord's own keys are pressed whether or not the user is holding them. Skipping a press
    /// because the key looks held would leave the shell waiting for a transition that never comes.
    /// </summary>
    [Fact]
    public void TheChordsOwnModifiersArePressedEvenWhenAlreadyHeld()
    {
        var sequence = ShellDesktopShortcuts.BuildChord(VIRTUAL_KEY.VK_F4, [VIRTUAL_KEY.VK_CONTROL]);

        Assert.Contains((VIRTUAL_KEY.VK_CONTROL, true), sequence);
    }
}
