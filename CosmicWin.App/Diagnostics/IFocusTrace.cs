namespace CosmicWin.App.Diagnostics;

/// <summary>
/// Sink for the per-keypress focus diagnostic. Implementations MUST NOT throw: this exists to
/// diagnose a defect in a running app, and a diagnostic that can crash its subject is worse than
/// no diagnostic at all.
/// </summary>
public interface IFocusTrace
{
    void Record(FocusTraceEntry entry);
}
