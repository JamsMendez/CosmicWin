using CosmicWin.App.Input;

namespace CosmicWin.App.Tests.Input;

public sealed class ChordTableTests
{
    public static TheoryData<ModifierKeys, KeyboardKey, HotkeyActionKind> RegisteredChords => new()
    {
        { ModifierKeys.Alt, KeyboardKey.H, HotkeyActionKind.FocusLeft },
        { ModifierKeys.Alt, KeyboardKey.Left, HotkeyActionKind.FocusLeft },
        { ModifierKeys.Alt, KeyboardKey.L, HotkeyActionKind.FocusRight },
        { ModifierKeys.Alt, KeyboardKey.Right, HotkeyActionKind.FocusRight },
        { ModifierKeys.Alt, KeyboardKey.K, HotkeyActionKind.FocusUp },
        { ModifierKeys.Alt, KeyboardKey.Up, HotkeyActionKind.FocusUp },
        { ModifierKeys.Alt, KeyboardKey.J, HotkeyActionKind.FocusDown },
        { ModifierKeys.Alt, KeyboardKey.Down, HotkeyActionKind.FocusDown },
        { ModifierKeys.Alt, KeyboardKey.CloseBracket, HotkeyActionKind.FocusIn },
        { ModifierKeys.Alt, KeyboardKey.OpenBracket, HotkeyActionKind.FocusOut },
        { ModifierKeys.Shift | ModifierKeys.Alt, KeyboardKey.H, HotkeyActionKind.MoveLeft },
        { ModifierKeys.Shift | ModifierKeys.Alt, KeyboardKey.Left, HotkeyActionKind.MoveLeft },
        { ModifierKeys.Shift | ModifierKeys.Alt, KeyboardKey.L, HotkeyActionKind.MoveRight },
        { ModifierKeys.Shift | ModifierKeys.Alt, KeyboardKey.Right, HotkeyActionKind.MoveRight },
        { ModifierKeys.Shift | ModifierKeys.Alt, KeyboardKey.K, HotkeyActionKind.MoveUp },
        { ModifierKeys.Shift | ModifierKeys.Alt, KeyboardKey.Up, HotkeyActionKind.MoveUp },
        { ModifierKeys.Shift | ModifierKeys.Alt, KeyboardKey.J, HotkeyActionKind.MoveDown },
        { ModifierKeys.Shift | ModifierKeys.Alt, KeyboardKey.Down, HotkeyActionKind.MoveDown },
        { ModifierKeys.Alt, KeyboardKey.O, HotkeyActionKind.ToggleOrientation },
        { ModifierKeys.Control | ModifierKeys.Alt, KeyboardKey.H, HotkeyActionKind.ResizeLeft },
        { ModifierKeys.Control | ModifierKeys.Alt, KeyboardKey.Left, HotkeyActionKind.ResizeLeft },
        { ModifierKeys.Control | ModifierKeys.Alt, KeyboardKey.L, HotkeyActionKind.ResizeRight },
        { ModifierKeys.Control | ModifierKeys.Alt, KeyboardKey.Right, HotkeyActionKind.ResizeRight },
        { ModifierKeys.Control | ModifierKeys.Alt, KeyboardKey.K, HotkeyActionKind.ResizeUp },
        { ModifierKeys.Control | ModifierKeys.Alt, KeyboardKey.Up, HotkeyActionKind.ResizeUp },
        { ModifierKeys.Control | ModifierKeys.Alt, KeyboardKey.J, HotkeyActionKind.ResizeDown },
        { ModifierKeys.Control | ModifierKeys.Alt, KeyboardKey.Down, HotkeyActionKind.ResizeDown },
    };

    [Theory]
    [MemberData(nameof(RegisteredChords))]
    public void TryMatch_RegisteredExactChord_ReturnsExpectedAction(
        ModifierKeys modifiers, KeyboardKey key, HotkeyActionKind expected)
    {
        Assert.True(ChordTable.Default.TryMatch(modifiers, key, out var action));
        Assert.Equal(expected, action.Kind);
    }

    [Theory]
    [InlineData(ModifierKeys.None, KeyboardKey.Menu)]
    [InlineData(ModifierKeys.Alt, KeyboardKey.Tab)]
    [InlineData(ModifierKeys.Alt, KeyboardKey.F4)]
    [InlineData(ModifierKeys.Alt, KeyboardKey.Space)]
    [InlineData(ModifierKeys.Alt, KeyboardKey.Escape)]
    [InlineData(ModifierKeys.Alt, KeyboardKey.Enter)]
    [InlineData(ModifierKeys.Control | ModifierKeys.Alt, KeyboardKey.Delete)]
    [InlineData(ModifierKeys.Control | ModifierKeys.Alt, KeyboardKey.O)]
    [InlineData(ModifierKeys.Shift | ModifierKeys.Alt, KeyboardKey.O)]
    [InlineData(ModifierKeys.Shift | ModifierKeys.Control | ModifierKeys.Alt, KeyboardKey.H)]
    [InlineData(ModifierKeys.Alt, (KeyboardKey)0x50)]
    public void TryMatch_NativeUnregisteredOrSupersetChord_ReturnsFalse(
        ModifierKeys modifiers, KeyboardKey key)
    {
        Assert.False(ChordTable.Default.TryMatch(modifiers, key, out _));
    }
}
