using CosmicWin.App.Input;

namespace CosmicWin.App.Tests.TestDoubles;

/// <summary>
/// Minimal <see cref="IKeyboardHookPlatform"/> double that drives a <see
/// cref="LowLevelKeyboardHook"/>'s internal callback without a real Win32 hook. Extracted from
/// <c>KeyboardHookTests</c> (WU7A) so <c>CompositionRootTests</c> can reuse it for V11-W1: proving
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

    public void RaiseActivity() => _callback!(KeyboardKey.H, true, ModifierKeys.Alt);

    /// <summary>Raises the given key/modifier combination through the installed callback, for scenarios that need a chord other than the default H+Alt (V11-W1: a key that is NOT a registered chord must still round-trip cleanly through the pause gate).</summary>
    public bool Raise(KeyboardKey key, bool isKeyDown, ModifierKeys modifiers) => _callback!(key, isKeyDown, modifiers);
}
