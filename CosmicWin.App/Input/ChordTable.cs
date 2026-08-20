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

    private void Register(ModifierKeys modifiers, KeyboardKey key, HotkeyActionKind kind)
    {
        var index = Index(modifiers, key);
        _actions[index] = new(kind);
        _registered[index] = true;
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
