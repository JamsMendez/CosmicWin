using CosmicWin.Interop;

namespace CosmicWin.Interop.Tests.TestDoubles;

/// <summary>
/// Hand-rolled in-memory <see cref="IWindow"/> used to pin down the contract before any
/// Win32-backed implementation exists. Intentionally avoids a third-party mocking library —
/// the Moq vs. NSubstitute decision is deferred to task 0.9 (design Open Questions).
/// </summary>
internal sealed class FakeWindow : IWindow
{
    private string _title;
    private Rectangle _bounds;

    public FakeWindow(IntPtr handle, string title, Rectangle bounds)
    {
        Handle = handle;
        _title = title;
        _bounds = bounds;
        IsAlive = true;
    }

    public IntPtr Handle { get; }

    public string Title => IsAlive ? _title : string.Empty;

    public Rectangle Bounds => IsAlive ? _bounds : Rectangle.Empty;

    public bool IsAlive { get; private set; }

    public void SetPosition(Rectangle bounds)
    {
        if (!IsAlive)
        {
            throw new InvalidOperationException("Cannot reposition a dead window.");
        }

        _bounds = bounds;
    }

    public void Rename(string title) => _title = title;

    public void Kill() => IsAlive = false;

    public bool Equals(IWindow? other) => other is not null && Handle == other.Handle;

    public override bool Equals(object? obj) => obj is IWindow other && Equals(other);

    public override int GetHashCode() => Handle.GetHashCode();
}
