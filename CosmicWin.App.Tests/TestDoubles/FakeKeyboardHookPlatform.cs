using CosmicWin.App.Input;

namespace CosmicWin.App.Tests.TestDoubles;

/// <summary>
/// Minimal <see cref="IKeyboardHookPlatform"/> double that drives a <see
/// cref="LowLevelKeyboardHook"/>'s internal callback without a real Win32 hook. Extracted from
/// <c>KeyboardHookTests</c> so <c>CompositionRootTests</c> can reuse it for: proving
/// the shared pause flag blocks both a real chord match AND a <c>WorkspaceSessionAdapter</c>
/// <c>WindowAdded</c> call from a single write, which needs an actual
/// <see cref="LowLevelKeyboardHook"/> driven end to end rather than the processor tested in
/// isolation.
/// </summary>
internal sealed class FakeKeyboardHookPlatform : IKeyboardHookPlatform
{
    public ManualResetEventSlim SecondInstall { get; } = new();
    public ApartmentState ApartmentState { get; private set; }
    public int CallbackThreadId { get; private set; }
    public int InstallCount => _installs;
    public int PumpCount => _pumps;
    public int UninstallCount => _uninstallCount;
    public bool UninstallResult { get; init; } = true;
    private KeyboardHookCallback? _callback;
    private int _installs;
    private int _pumps;
    private int _uninstallCount;

    public void Install(KeyboardHookCallback callback)
    {
        _callback = callback;
        ApartmentState = Thread.CurrentThread.GetApartmentState();
        CallbackThreadId = Environment.CurrentManagedThreadId;
        callback(KeyboardKey.H, true, ModifierKeys.Alt);
        if (Interlocked.Increment(ref _installs) >= 2) SecondInstall.Set();
    }

    public bool Uninstall()
    {
        Interlocked.Increment(ref _uninstallCount);
        return UninstallResult;
    }

    public void PumpMessages() => Interlocked.Increment(ref _pumps);

    /// <summary>
    /// How long ago the SESSION last saw input. Defaults to "just now", which is the reading that
    /// makes a silent hook suspicious; a test about an idle machine raises it.
    /// </summary>
    public long? SystemInputAge { get; set; }

    public long? MillisecondsSinceSystemInput() => RefuseSystemInputQuestion ? null : SystemInputAge ?? 0;

    /// <summary>Makes the shell refuse the question, the way it does off the interactive desktop.</summary>
    public bool RefuseSystemInputQuestion { get; set; }

    /// <summary>Where the cursor is, for tests that need to prove a move (or its absence) was noticed. Defaults to a still cursor, so every existing test keeps its current meaning.</summary>
    public (int X, int Y) Cursor { get; set; }

    /// <summary>Makes every reading report a cursor one step further along, simulating a mouse in motion.</summary>
    public bool MoveCursorOnEveryReading { get; set; }

    /// <summary>Makes the shell refuse the question, the way <see cref="RefuseSystemInputQuestion"/> does for system input.</summary>
    public bool RefuseCursorQuestion { get; set; }

    /// <summary>
    /// How many times the cursor has been read. A test driving a FAKE clock needs this: the clock
    /// jumps where the real one creeps, so it must wait for the loop's baseline sample -- and the
    /// first move after it -- to exist before it can jump past the watchdog interval.
    /// </summary>
    public int CursorReadCount => _cursorReads;
    private int _cursorReads;

    public (int X, int Y)? CursorPosition()
    {
        Interlocked.Increment(ref _cursorReads);
        if (RefuseCursorQuestion) return null;
        if (MoveCursorOnEveryReading) Cursor = (Cursor.X + 1, Cursor.Y);
        return Cursor;
    }

    public void RaiseActivity() => _callback!(KeyboardKey.H, true, ModifierKeys.Alt);

    /// <summary>Raises the given key/modifier combination through the installed callback, for scenarios that need a chord other than the default H+Alt (: a key that is NOT a registered chord must still round-trip cleanly through the pause gate).</summary>
    public bool Raise(KeyboardKey key, bool isKeyDown, ModifierKeys modifiers) => _callback!(key, isKeyDown, modifiers);
}
