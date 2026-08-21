namespace CosmicWin.Layout.Filters;

/// <summary>
/// Mutable holder for the currently active <see cref="ExceptionList"/> (spec WE-3). Callers keep
/// one long-lived <see cref="ExceptionListStore"/> reference and call <see cref="Reload"/> when
/// the on-disk exception-list file changes (tray "Reload" or file-watch — the actual trigger and
/// file I/O are an App-layer concern, out of this batch's Win32-free scope); every reader that
/// only holds the store (not a snapshot of <see cref="Current"/>) observes the update immediately,
/// with no restart and no re-wiring.
/// </summary>
public sealed class ExceptionListStore
{
    public ExceptionListStore(ExceptionList initial) => Current = initial;

    /// <summary>The exception list currently in effect.</summary>
    public ExceptionList Current { get; private set; }

    /// <summary>Re-parses <paramref name="content"/> and replaces <see cref="Current"/> with the result.</summary>
    public void Reload(string content) => Current = ExceptionListLoader.Parse(content);

    /// <summary>Task 3.37: replaces <see cref="Current"/> with an already-parsed list (e.g. from an on-disk file loader), for callers that read raw text themselves rather than handing it to this store.</summary>
    public void Reload(ExceptionList replacement) => Current = replacement;
}
