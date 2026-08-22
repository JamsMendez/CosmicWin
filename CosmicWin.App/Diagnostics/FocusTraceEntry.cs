using CosmicWin.Layout;

namespace CosmicWin.App.Diagnostics;

/// <summary>
/// One focus chord, recorded whole: the direction pressed, the leaf focus started from, the leaf
/// the tree walk selected (zero when there was none), and what became of it.
/// </summary>
public readonly record struct FocusTraceEntry(
    Direction Direction,
    nint FocusedHandle,
    nint TargetHandle,
    FocusTraceOutcome Outcome);
