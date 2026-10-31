using System.Threading.Channels;
using CosmicWin.App.Input;
using CosmicWin.App.Tests.TestDoubles;

namespace CosmicWin.App.Tests.Input;

/// <summary>
/// The chord table speaks Alt; <see cref="KeyboardEventProcessor.UseWinAsChordModifier"/> decides
/// which PHYSICAL key reads as it. These pin both readings and the Start-menu mask the Win one needs.
/// </summary>
public sealed class WinChordModifierTests
{
    [Theory]
    [InlineData(KeyboardKey.L, ModifierKeys.Win, HotkeyActionKind.FocusRight, 0)]
    [InlineData(KeyboardKey.H, ModifierKeys.Win | ModifierKeys.Shift, HotkeyActionKind.MoveLeft, 0)]
    [InlineData(KeyboardKey.K, ModifierKeys.Win | ModifierKeys.Control, HotkeyActionKind.ResizeUp, 0)]
    [InlineData(KeyboardKey.D2, ModifierKeys.Win | ModifierKeys.Shift, HotkeyActionKind.MoveWindowToDesktop, 2)]
    [InlineData(KeyboardKey.T, ModifierKeys.Win, HotkeyActionKind.ToggleTiling, 0)]
    public void InWinMode_WinChords_MatchTheTableWrittenForAlt(
        KeyboardKey key, ModifierKeys modifiers, HotkeyActionKind expected, int argument)
    {
        var channel = Channel.CreateUnbounded<HotkeyAction>();
        var processor = new KeyboardEventProcessor(ChordTable.Default) { UseWinAsChordModifier = true };

        Assert.True(processor.Process(key, true, modifiers, channel.Writer));
        Assert.True(channel.Reader.TryRead(out var action));
        Assert.Equal(new HotkeyAction(expected, argument), action);
    }

    [Theory]
    [InlineData(ModifierKeys.Alt)]
    [InlineData(ModifierKeys.Alt | ModifierKeys.Shift)]
    public void InWinMode_AltChords_GoBackToTheApplications(ModifierKeys modifiers)
    {
        var channel = Channel.CreateUnbounded<HotkeyAction>();
        var processor = new KeyboardEventProcessor(ChordTable.Default) { UseWinAsChordModifier = true };

        Assert.False(processor.Process(KeyboardKey.L, true, modifiers, channel.Writer));
        Assert.False(channel.Reader.TryRead(out _));
    }

    /// <summary>Off, a held Win changes nothing: it was never reported before, and Alt still rules.</summary>
    [Fact]
    public void InAltMode_WinIsIgnored_AndAltStillMatches()
    {
        var channel = Channel.CreateUnbounded<HotkeyAction>();
        var processor = new KeyboardEventProcessor(ChordTable.Default);

        Assert.False(processor.Process(KeyboardKey.L, true, ModifierKeys.Win, channel.Writer));
        Assert.True(processor.Process(KeyboardKey.L, true, ModifierKeys.Win | ModifierKeys.Alt, channel.Writer));
        Assert.True(channel.Reader.TryRead(out var action));
        Assert.Equal(HotkeyActionKind.FocusRight, action.Kind);
        Assert.False(channel.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData(KeyboardKey.LeftWin)]
    [InlineData(KeyboardKey.RightWin)]
    public void InWinMode_ReleasingWinAfterAChord_MasksTheStartMenuOnce(KeyboardKey win)
    {
        var masks = 0;
        var channel = Channel.CreateUnbounded<HotkeyAction>();
        var processor = new KeyboardEventProcessor(ChordTable.Default)
        {
            UseWinAsChordModifier = true,
            MaskWinRelease = () => masks++,
        };

        processor.Process(KeyboardKey.L, true, ModifierKeys.Win, channel.Writer);
        processor.Process(KeyboardKey.L, false, ModifierKeys.Win, channel.Writer);

        // The release itself still reaches Windows: only the Start menu it would open is masked.
        Assert.False(processor.Process(win, false, ModifierKeys.None, channel.Writer));
        Assert.Equal(1, masks);

        processor.Process(win, false, ModifierKeys.None, channel.Writer);
        Assert.Equal(1, masks);
    }

    /// <summary>A Win tapped on its own is the user asking for the Start menu, and gets it.</summary>
    [Fact]
    public void InWinMode_ReleasingWinWithNoChord_MasksNothing()
    {
        var masks = 0;
        var channel = Channel.CreateUnbounded<HotkeyAction>();
        var processor = new KeyboardEventProcessor(ChordTable.Default)
        {
            UseWinAsChordModifier = true,
            MaskWinRelease = () => masks++,
        };

        processor.Process(KeyboardKey.LeftWin, true, ModifierKeys.Win, channel.Writer);
        processor.Process(KeyboardKey.M, true, ModifierKeys.Win, channel.Writer);
        processor.Process(KeyboardKey.LeftWin, false, ModifierKeys.None, channel.Writer);

        Assert.Equal(0, masks);
    }

    [Fact]
    public void InAltMode_ReleasingWinAfterAChord_MasksNothing()
    {
        var masks = 0;
        var channel = Channel.CreateUnbounded<HotkeyAction>();
        var processor = new KeyboardEventProcessor(ChordTable.Default) { MaskWinRelease = () => masks++ };

        processor.Process(KeyboardKey.L, true, ModifierKeys.Alt | ModifierKeys.Win, channel.Writer);
        processor.Process(KeyboardKey.LeftWin, false, ModifierKeys.Alt, channel.Writer);

        Assert.Equal(0, masks);
    }

    /// <summary>The mask's own keystroke comes back through the hook, and must not read as a failed chord.</summary>
    [Fact]
    public void TheMaskKey_PassesThroughWithoutBeingRecorded()
    {
        var channel = Channel.CreateUnbounded<HotkeyAction>();
        var processor = new KeyboardEventProcessor(ChordTable.Default) { UseWinAsChordModifier = true };

        Assert.False(processor.Process(KeyboardKey.MaskKey, true, ModifierKeys.Win, channel.Writer));
        Assert.Null(processor.LastUnmatched);
    }

    [Fact]
    public void UseWinAsChordModifier_OnLowLevelKeyboardHook_PassesThroughToUnderlyingProcessor()
    {
        var channel = Channel.CreateUnbounded<HotkeyAction>();
        var platform = new FakeKeyboardHookPlatform();
        using var hook = new LowLevelKeyboardHook(channel.Writer, platform, TimeSpan.FromMinutes(1));
        hook.Start();
        while (channel.Reader.TryRead(out _)) { }

        hook.UseWinAsChordModifier = true;

        Assert.True(hook.UseWinAsChordModifier);
        Assert.True(platform.Raise(KeyboardKey.L, true, ModifierKeys.Win));
        Assert.False(platform.Raise(KeyboardKey.H, true, ModifierKeys.Alt));
        platform.Raise(KeyboardKey.LeftWin, false, ModifierKeys.None);
        Assert.Equal(1, platform.MaskCount);
    }
}
