using System.Diagnostics;

namespace CosmicWin.App.Tests.TestDoubles;

/// <summary>
/// Registers itself on <see cref="Trace.Listeners"/> for the lifetime of the using block and keeps
/// every line written through <see cref="Trace.WriteLine(string)"/>. The listener list is
/// process-wide and tests run in parallel, so assertions must match on text unique to the test.
/// </summary>
internal sealed class CapturingTraceListener : TraceListener
{
    private readonly List<string> _lines = [];

    public CapturingTraceListener() => Trace.Listeners.Add(this);

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
            {
                return [.. _lines];
            }
        }
    }

    public override void Write(string? message) => WriteLine(message);

    public override void WriteLine(string? message)
    {
        lock (_lines)
        {
            _lines.Add(message ?? string.Empty);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Trace.Listeners.Remove(this);
        }

        base.Dispose(disposing);
    }
}
