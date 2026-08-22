using CosmicWin.App.Diagnostics;

namespace CosmicWin.App.Tests.TestDoubles;

/// <summary>
/// In-memory <see cref="IFocusTrace"/> that keeps every recorded entry in order, so tests assert
/// the exact diagnostic a supervised run will read off disk instead of asserting the file format.
/// </summary>
internal sealed class RecordingFocusTrace : IFocusTrace
{
    private readonly List<FocusTraceEntry> _entries = [];

    public IReadOnlyList<FocusTraceEntry> Entries => _entries;

    public void Record(FocusTraceEntry entry) => _entries.Add(entry);
}
