using CosmicWin.Interop;

namespace CosmicWin.Interop.Tests.TestDoubles;

/// <summary>
/// Hand-rolled in-memory <see cref="IWindow"/> used to pin down the contract before any
/// Win32-backed implementation exists. Intentionally avoids a third-party mocking library —
/// the Moq vs. NSubstitute decision is deferred to task 0.9 (design Open Questions).
/// </summary>
internal sealed class FakeWindow : IWindow
{
    /// <summary>Task 3.28 regression-safety default — see <c>RecordingWindow</c>'s identical constant for the rationale.</summary>
    private const uint TileableStyleDefault = 0x00080000u | 0x00010000u | 0x00020000u;

    private string _title;
    private Rectangle _bounds;
    private bool _failNextSetPosition;
    private bool _failNextActivate;

    public FakeWindow(
        IntPtr handle,
        string title,
        Rectangle bounds,
        string className = "",
        string processName = "",
        uint style = TileableStyleDefault,
        uint exStyle = 0u,
        bool isOwned = false)
    {
        Handle = handle;
        _title = title;
        _bounds = bounds;
        IsAlive = true;
        CanReposition = true;
        ClassName = className;
        ProcessName = processName;
        Style = style;
        ExStyle = exStyle;
        IsOwned = isOwned;
    }

    public IntPtr Handle { get; }

    public string Title => IsAlive ? _title : string.Empty;

    public Rectangle Bounds => IsAlive ? _bounds : Rectangle.Empty;

    public bool IsAlive { get; private set; }

    public bool CanReposition { get; private set; }

    public string ClassName { get; private set; }

    public string ProcessName { get; private set; }

    public uint Style { get; private set; }

    public uint ExStyle { get; private set; }

    public bool IsOwned { get; private set; }

    public void SetPosition(Rectangle bounds)
    {
        if (!IsAlive)
        {
            throw new InvalidOperationException("Cannot reposition a dead window.");
        }

        if (!CanReposition)
        {
            return;
        }

        if (_failNextSetPosition)
        {
            _failNextSetPosition = false;
            CanReposition = false;
            return;
        }

        _bounds = bounds;
    }

    /// <summary>Makes the next <see cref="SetPosition"/> call fail (threat matrix: cross-process window manipulation).</summary>
    public void FailNextSetPosition() => _failNextSetPosition = true;

    public bool TryActivate()
    {
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

    /// <summary>Makes the next <see cref="TryActivate"/> call fail without throwing.</summary>
    public void FailNextActivate() => _failNextActivate = true;

    public void Rename(string title) => _title = title;

    public void Kill() => IsAlive = false;

    public bool Equals(IWindow? other) => other is not null && Handle == other.Handle;

    public override bool Equals(object? obj) => obj is IWindow other && Equals(other);

    public override int GetHashCode() => Handle.GetHashCode();
}
