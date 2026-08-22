namespace CosmicWin.App.Diagnostics;

/// <summary>
/// The single fact a focus chord produced, ordered along <c>ActionExecutor</c>'s focus path. Every
/// value below is externally indistinguishable from the others on real hardware -- the window just
/// does not change -- which is exactly why MR-2 could not be diagnosed by observation (Engram
/// discovery #101) and why the outcome must be recorded rather than inferred.
/// </summary>
public enum FocusTraceOutcome
{
    /// <summary>No tracked leaf resolved, so the chord never reached the tree walk at all.</summary>
    UnresolvedFocus,

    /// <summary><c>NextFocus</c> reached the tree root without a match (LE-2 step 4).</summary>
    NoMatch,

    /// <summary>A target leaf was found, but no live window is registered for its handle.</summary>
    UntrackedTarget,

    /// <summary>The target window was asked to activate and reported failure.</summary>
    ActivateFailed,

    /// <summary>The target window reported a successful activation.</summary>
    Activated
}
