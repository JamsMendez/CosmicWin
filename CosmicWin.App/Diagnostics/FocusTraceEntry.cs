using CosmicWin.Interop;
using CosmicWin.Layout;

namespace CosmicWin.App.Diagnostics;

/// <summary>
/// One focus chord, recorded whole: the direction pressed, the window the OS actually reported as
/// foreground, the leaf focus started from, the leaf the tree walk selected (zero when there was
/// none), and what became of it.
/// </summary>
/// <remarks>
/// <para>
/// <paramref name="ForegroundHandle"/> is what makes an <see cref="FocusTraceOutcome.Activated"/>
/// line trustworthy. <c>Win32NativeWindowSource.Activate</c>
/// short-circuits to success when the target ALREADY holds the foreground, so without this field a
/// chord that activated the window the user was already on is indistinguishable from a chord that
/// genuinely moved focus -- both just say "Activated" while only one of them changed the screen.
/// </para>
/// <para>
/// <paramref name="Activation"/> is the CONTROL GROUP for the desktop handover's own rung reading.
/// A defect attributed to the activation escalation has to explain why an ordinary focus chord does
/// not show it, and that explanation is a comparison between two rungs -- one recorded here, one on
/// the handover line. Either alone proves nothing.
/// </para>
/// </remarks>
/// <param name="Activation">
/// Which rung of the escalation answered, or <see langword="null"/> when the chord never reached an
/// activation at all. Null rather than a default value because
/// <see cref="ActivationOutcome"/>'s zero is <see cref="ActivationOutcome.AlreadyForeground"/>,
/// which would read as a claim about a target that was never even found.
/// </param>
public readonly record struct FocusTraceEntry(
    Direction Direction,
    nint ForegroundHandle,
    nint FocusedHandle,
    nint TargetHandle,
    FocusTraceOutcome Outcome,
    ActivationOutcome? Activation = null);
