namespace CosmicWin.Interop;

/// <summary>
/// Which rung of <c>Win32NativeWindowSource.Activate</c>'s escalation actually moved the OS
/// foreground. Recorded rather than collapsed to a boolean so a later run can tell which rung is
/// doing the work.
///
/// MEASURED on the development machine (Windows 11 26200), activating between two real
/// external windows: the reported rung was <see cref="InputUnlocked"/>. Both <see cref="Direct"/>
/// and <see cref="AttachedInput"/> were refused, so the synthetic Alt taps are not a defensive
/// extra -- they are the ONLY rung that works here, and removing them puts focus navigation back
/// exactly where MR-2 left it. Re-measure before deleting any rung.
/// </summary>
/// <remarks>
/// <para>
/// Every <see cref="ActivationOutcomeExtensions.Confirmed"/> value means the same verified thing:
/// <c>GetForegroundWindow</c> reported the target AFTER the attempt. A bare
/// <c>SetForegroundWindow</c> return value is not trusted — MR-2's fourth supervised run showed
/// that call reporting success while nothing moved.
/// </para>
/// <para>
/// PUBLIC, and in <c>CosmicWin.Interop</c> rather than <c>CosmicWin.Interop.Win32</c>, because
/// <see cref="IWindow.Activate"/> now carries it out of the assembly. It stopped being a Win32
/// implementation detail the moment the App layer needed to write it into a trace: which rung ran
/// is the diagnosis, and an internal enum could only ever reach the log as a boolean.
/// </para>
/// </remarks>
public enum ActivationOutcome
{
    /// <summary>Nothing to do: the target already held the foreground.</summary>
    AlreadyForeground,

    /// <summary>A plain <c>SetForegroundWindow</c> was enough.</summary>
    Direct,

    /// <summary>Succeeded after attaching to the foreground thread's input queue.</summary>
    AttachedInput,

    /// <summary>Succeeded only after synthetic Alt taps released Windows' foreground lock.</summary>
    InputUnlocked,

    /// <summary>Every rung was refused; the foreground still belongs to someone else.</summary>
    Failed,

    /// <summary>
    /// The bounded wait expired before the worker finished. NOT a refusal: on this path no rung was
    /// evaluated, and under load the worker may never have been scheduled at all.
    /// </summary>
    /// <remarks>
    /// Split out of <see cref="Failed"/> because collapsing the two made every red activation
    /// unattributable -- the reason this repository's desktop flakiness resisted diagnosis for so
    /// long. The two readings call for opposite fixes: <see cref="Failed"/> is Windows saying no,
    /// this is our own budget being too short for the machine it ran on.
    /// </remarks>
    TimedOut
}

/// <summary>Reads an <see cref="ActivationOutcome"/> the way a caller who only needs a yes/no does.</summary>
public static class ActivationOutcomeExtensions
{
    /// <summary>
    /// Whether the OS CONFIRMED the target holds the foreground.
    /// </summary>
    /// <remarks>
    /// The single place that judgement is made, so the boolean every caller still uses and the rung
    /// the trace records can never come from different code. Both failing endings answer
    /// <see langword="false"/>: a timeout confirms nothing, so the split serves the diagnosis and
    /// never changes what a caller sees.
    /// </remarks>
    public static bool Confirmed(this ActivationOutcome outcome) =>
        outcome is not (ActivationOutcome.Failed or ActivationOutcome.TimedOut);
}
