namespace CosmicWin.Interop.Win32;

/// <summary>
/// The ordering and fallback policy of one tinted frame, separated from COM so it is testable. The
/// player supplies the real transfers; a tint problem can never cost the frame because every path
/// that does not end in a successful tinted compose ends in exactly one plain transfer.
/// </summary>
internal static class TintedFrameTransfer
{
    /// <param name="tryBegin">Driver <c>TryBegin</c> (true only with a usable target).</param>
    /// <param name="transferIntoTarget">Transfers the video frame into the tint pass's intermediate texture.</param>
    /// <param name="complete">Driver <c>Complete</c>: composes the intermediate into the back buffer.</param>
    /// <param name="abort">Driver <c>Abort</c>: something threw; back off.</param>
    /// <param name="transferPlain">Transfers the frame straight into the back buffer.</param>
    public static void Run(
        Func<bool> tryBegin, Action transferIntoTarget, Func<bool> complete, Action<Exception> abort, Action transferPlain)
    {
        bool tinted = false;
        try
        {
            if (tryBegin())
            {
                transferIntoTarget();
                tinted = complete();
            }
        }
        catch (Exception ex)
        {
            abort(ex);
        }

        if (!tinted)
        {
            transferPlain();
        }
    }
}
