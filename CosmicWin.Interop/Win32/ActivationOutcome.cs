namespace CosmicWin.Interop.Win32;

/// <summary>
/// Which rung of <see cref="Win32NativeWindowSource.Activate"/>'s escalation actually moved the OS
/// foreground. Recorded rather than collapsed to a boolean so a later run can tell whether the
/// synthetic-input rung is still earning its cost, or can be dropped.
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
