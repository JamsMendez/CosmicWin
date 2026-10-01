namespace CosmicWin.Interop;

/// <summary>
/// How a bounded style write on another process's window ended. Three endings, because the two
/// failing ones mean OPPOSITE things about the window's state.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Refused"/> is a PROOF: the call completed and the style read back shows the window
/// still has what it had (an elevated target, an app that overrides the bit). Nothing changed and
/// nothing will.
/// </para>
/// <para>
/// <see cref="TimedOut"/> is the absence of an answer. The write runs on a worker that cannot be
/// cancelled, so it may still land after the caller has moved on. A caller that treats the two
/// alike either leaves a disabled button nobody gave back or judges a window by a style that is
/// about to change. PUBLIC, and in <c>CosmicWin.Interop</c>, because <see cref="IWindow"/> carries
/// it out of the assembly (the same reason <see cref="ActivationOutcome"/> is).
/// </para>
/// </remarks>
public enum StyleWriteOutcome
{
    /// <summary>The window ended up in the requested state, confirmed by reading the style back.</summary>
    Applied,

    /// <summary>The call completed and the window did not take the change (or is gone).</summary>
    Refused,

    /// <summary>No answer within the bound; the abandoned write may still land later.</summary>
    TimedOut,
}
