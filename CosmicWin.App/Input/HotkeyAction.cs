namespace CosmicWin.App.Input;

[Flags]
public enum ModifierKeys : byte
{
    None = 0,
    Alt = 1,
    Shift = 2,
    Control = 4,
}

public enum KeyboardKey : byte
{
    Tab = 0x09, Enter = 0x0D, Menu = 0x12, Escape = 0x1B, Space = 0x20,
    Left = 0x25, Up = 0x26, Right = 0x27, Down = 0x28, Delete = 0x2E,
    H = 0x48, J = 0x4A, K = 0x4B, L = 0x4C, O = 0x4F,
    OpenBracket = 0xDB, CloseBracket = 0xDD, F4 = 0x73,
}

public enum HotkeyActionKind : byte
{
    FocusLeft, FocusRight, FocusUp, FocusDown, FocusIn, FocusOut,
    MoveLeft, MoveRight, MoveUp, MoveDown, ToggleOrientation,
    ResizeLeft, ResizeRight, ResizeUp, ResizeDown,
}

public readonly record struct HotkeyAction(HotkeyActionKind Kind);
