namespace CosmicWin.Interop.Win32;

/// <summary>
/// Which rung of <see cref="Win32NativeWindowSource.Activate"/>'s escalation actually moved the OS
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
/// Every non-<see cref="Failed"/> value means the same verified thing: <c>GetForegroundWindow</c>
/// reported the target AFTER the attempt. A bare <c>SetForegroundWindow</c> return value is not
/// trusted — MR-2's fourth supervised run showed that call reporting success while nothing moved.
/// </remarks>
internal enum ActivationOutcome
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
    Failed
}
