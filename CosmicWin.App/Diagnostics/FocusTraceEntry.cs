using CosmicWin.Layout;

namespace CosmicWin.App.Diagnostics;

/// <summary>
/// One focus chord, recorded whole: the direction pressed, the window the OS actually reported as
/// foreground, the leaf focus started from, the leaf the tree walk selected (zero when there was
/// none), and what became of it.
/// </summary>
/// <remarks>
/// <paramref name="ForegroundHandle"/> is what makes an <see cref="FocusTraceOutcome.Activated"/>
/// line trustworthy (Engram discovery #104). <c>Win32NativeWindowSource.TryActivateWindow</c>
/// short-circuits to success when the target ALREADY holds the foreground, so without this field a
/// chord that activated the window the user was already on is indistinguishable from a chord that
/// genuinely moved focus -- both just say "Activated" while only one of them changed the screen.
/// </remarks>
public readonly record struct FocusTraceEntry(
    Direction Direction,
    nint ForegroundHandle,
    nint FocusedHandle,
    nint TargetHandle,
    FocusTraceOutcome Outcome);
