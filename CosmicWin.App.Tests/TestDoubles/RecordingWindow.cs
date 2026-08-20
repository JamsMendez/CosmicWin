using CosmicWin.Interop;

namespace CosmicWin.App.Tests.TestDoubles;

/// <summary>
/// Minimal in-memory <see cref="IWindow"/> used by App-layer tests to observe how many times
/// <see cref="SetPosition"/>/<see cref="TryActivate"/> were invoked, and to force either call to
/// fail without throwing. Mirrors the Interop.Tests <c>FakeWindow</c> shape without depending on
/// that internal test-only type across assemblies.
/// </summary>
internal sealed class RecordingWindow : IWindow
{
    private bool _failNextSetPosition;
    private bool _failNextActivate;

    public RecordingWindow(nint handle, Rectangle bounds)
    {
        Handle = handle;
        Bounds = bounds;
        IsAlive = true;
        CanReposition = true;
    }

    public nint Handle { get; }

    public string Title => IsAlive ? "Recording" : string.Empty;

    public Rectangle Bounds { get; private set; }

    public bool IsAlive { get; private set; }

    public bool CanReposition { get; private set; }

    public int SetPositionCallCount { get; private set; }

    public int TryActivateCallCount { get; private set; }

    public Rectangle? LastSetPosition { get; private set; }

    public void SetPosition(Rectangle bounds)
    {
        SetPositionCallCount++;
        if (!IsAlive || !CanReposition)
        {
            return;
        }

        if (_failNextSetPosition)
        {
            _failNextSetPosition = false;
            CanReposition = false;
            return;
        }

        Bounds = bounds;
        LastSetPosition = bounds;
    }

    public bool TryActivate()
    {
        TryActivateCallCount++;
        if (!IsAlive)
        {
            return false;
        }

        if (_failNextActivate)
        {
            _failNextActivate = false;
            return false;
        }

        return true;
    }

    /// <summary>Makes the next <see cref="SetPosition"/> call fail (threat matrix precedent).</summary>
    public void FailNextSetPosition() => _failNextSetPosition = true;

    /// <summary>Makes the next <see cref="TryActivate"/> call fail without throwing.</summary>
    public void FailNextActivate() => _failNextActivate = true;

    public void Kill()
    {
        IsAlive = false;
        Bounds = Rectangle.Empty;
    }

    public bool Equals(IWindow? other) => other is not null && Handle == other.Handle;

    public override bool Equals(object? obj) => obj is IWindow other && Equals(other);

    public override int GetHashCode() => Handle.GetHashCode();
}
