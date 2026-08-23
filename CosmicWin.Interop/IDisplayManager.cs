namespace CosmicWin.Interop;

/// <summary>
/// Enumerates the displays attached to the system. Shaped after the reference implementation's
/// <c>IDisplayManager</c>,
/// trimmed to what multi-monitor-tiling (MM-1..5, later work units) requires.
/// </summary>
public interface IDisplayManager
{
    /// <summary>A snapshot of all attached displays. MUST always contain <see cref="Primary"/>.</summary>
    IReadOnlyList<IDisplay> Displays { get; }

    /// <summary>The current primary display.</summary>
    IDisplay Primary { get; }
}
