namespace CosmicWin.App.Input;

public sealed class ChordTable
{
    private const int ModifierCount = 8;
    private readonly HotkeyAction[] _actions = new HotkeyAction[256 * ModifierCount];
    private readonly bool[] _registered = new bool[256 * ModifierCount];

    public static ChordTable Default { get; } = CreateDefault();

    private ChordTable() { }

    public bool TryMatch(ModifierKeys modifiers, KeyboardKey key, out HotkeyAction action)
    {
        var index = Index(modifiers, key);
        action = _actions[index];
        return _registered[index];
    }

    private void Register(ModifierKeys modifiers, KeyboardKey key, HotkeyActionKind kind, int argument = 0)
    {
        var index = Index(modifiers, key);
        _actions[index] = new(kind, argument);
        _registered[index] = true;
    }

    /// <summary>
    /// <c>Alt+1</c>..<c>Alt+9</c> select a virtual desktop by POSITION, and <c>Alt+Shift+N</c> sends
    /// the focused window there. The number rides along as the action's argument, so the dispatcher
    /// needs one case rather than nine.
    /// </summary>
    private void RegisterDesktopDigits()
    {
        for (var number = 1; number <= 9; number++)
        {
            var key = (KeyboardKey)((byte)KeyboardKey.D1 + number - 1);
            Register(ModifierKeys.Alt, key, HotkeyActionKind.SwitchDesktop, number);
            Register(ModifierKeys.Shift | ModifierKeys.Alt, key, HotkeyActionKind.MoveWindowToDesktop, number);
        }
    }

    private static int Index(ModifierKeys modifiers, KeyboardKey key) =>
        ((int)key * ModifierCount) + (int)modifiers;

    private static ChordTable CreateDefault()
    {
        var table = new ChordTable();
        table.RegisterDirections(ModifierKeys.Alt,
            HotkeyActionKind.FocusLeft, HotkeyActionKind.FocusRight,
            HotkeyActionKind.FocusUp, HotkeyActionKind.FocusDown);
        table.RegisterDirections(ModifierKeys.Shift | ModifierKeys.Alt,
            HotkeyActionKind.MoveLeft, HotkeyActionKind.MoveRight,
            HotkeyActionKind.MoveUp, HotkeyActionKind.MoveDown);
        table.RegisterDirections(ModifierKeys.Control | ModifierKeys.Alt,
            HotkeyActionKind.ResizeLeft, HotkeyActionKind.ResizeRight,
            HotkeyActionKind.ResizeUp, HotkeyActionKind.ResizeDown);
        table.Register(ModifierKeys.Alt, KeyboardKey.CloseBracket, HotkeyActionKind.FocusIn);
        table.Register(ModifierKeys.Alt, KeyboardKey.OpenBracket, HotkeyActionKind.FocusOut);
        table.Register(ModifierKeys.Alt, KeyboardKey.O, HotkeyActionKind.ToggleOrientation);
        table.Register(ModifierKeys.Alt, KeyboardKey.Q, HotkeyActionKind.CloseWindow);

        // Shift over the SAME key that closes a window, the way Shift over a desktop digit turns
        // "go there" into "send there". Closing the desktop is the bigger version of closing the
        // thing in front of you, and one modifier keeping one meaning is worth more than a mnemonic.
        table.Register(ModifierKeys.Shift | ModifierKeys.Alt, KeyboardKey.Q, HotkeyActionKind.CloseDesktop);
        table.RegisterDesktopDigits();
        return table;
    }

    private void RegisterDirections(
        ModifierKeys modifiers, HotkeyActionKind left, HotkeyActionKind right,
        HotkeyActionKind up, HotkeyActionKind down)
    {
        Register(modifiers, KeyboardKey.H, left);
        Register(modifiers, KeyboardKey.Left, left);
        Register(modifiers, KeyboardKey.L, right);
        Register(modifiers, KeyboardKey.Right, right);
        Register(modifiers, KeyboardKey.K, up);
        Register(modifiers, KeyboardKey.Up, up);
        Register(modifiers, KeyboardKey.J, down);
        Register(modifiers, KeyboardKey.Down, down);
    }
}
