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
    H = 0x48, J = 0x4A, K = 0x4B, L = 0x4C, O = 0x4F, Q = 0x51,
    OpenBracket = 0xDB, CloseBracket = 0xDD, F4 = 0x73,
    D1 = 0x31, D2 = 0x32, D3 = 0x33, D4 = 0x34, D5 = 0x35,
    D6 = 0x36, D7 = 0x37, D8 = 0x38, D9 = 0x39,
}

public enum HotkeyActionKind : byte
{
    FocusLeft, FocusRight, FocusUp, FocusDown, FocusIn, FocusOut,
    MoveLeft, MoveRight, MoveUp, MoveDown, ToggleOrientation,
    ResizeLeft, ResizeRight, ResizeUp, ResizeDown,
    SwitchDesktop, MoveWindowToDesktop, CloseWindow, CloseDesktop,
}

/// <summary>
/// One dispatched chord. <paramref name="Argument"/> carries the desktop number for
/// <see cref="HotkeyActionKind.SwitchDesktop"/> and
/// <see cref="HotkeyActionKind.MoveWindowToDesktop"/>, and is ignored by every other kind.
/// </summary>
/// <remarks>
/// A payload rather than eighteen more enum members. Nine desktops times two intents would have
/// doubled <see cref="HotkeyActionKind"/> to encode a number the chord table already knows at
/// registration time, and every switch over it would have grown the same way.
/// </remarks>
public readonly record struct HotkeyAction(HotkeyActionKind Kind, int Argument = 0);
