namespace CosmicWin.Interop.Win32;

/// <summary>
/// Minimal <see cref="IWindow"/> backed by <see cref="INativeWindowSource"/>, owned by
/// <see cref="Win32Workspace"/>.
/// </summary>
/// <remarks>
/// Task 0.6 (WU2, out of this work unit's scope) adds a dedicated <c>Win32Window.cs</c> with
/// PerMonitorV2 DPI correctness (WT-2) and <c>SetPosition</c> failure → untileable degradation
/// (threat matrix: cross-process window manipulation). Until then this stays intentionally
/// minimal: it satisfies WT-1 (tracking) only.
/// </remarks>
internal sealed class TrackedWindow : IWindow
{
    private readonly INativeWindowSource _nativeSource;
    private string _title;
    private Rectangle _bounds;

    public TrackedWindow(nint handle, string title, Rectangle bounds, INativeWindowSource nativeSource)
    {
        Handle = handle;
        _title = title;
        _bounds = bounds;
        _nativeSource = nativeSource;
        IsAlive = true;
    }

    public nint Handle { get; }

    public string Title => IsAlive ? _title : string.Empty;

    public Rectangle Bounds => IsAlive ? _bounds : Rectangle.Empty;

    public bool IsAlive { get; private set; }

    public void SetPosition(Rectangle bounds)
    {
        if (!IsAlive)
        {
            return;
        }

        _nativeSource.SetWindowPosition(Handle, bounds);
        _bounds = bounds;
    }

    internal void Refresh(string title, Rectangle bounds)
    {
        _title = title;
        _bounds = bounds;
    }

    internal void MarkDead() => IsAlive = false;

    public bool Equals(IWindow? other) => other is not null && Handle == other.Handle;

    public override bool Equals(object? obj) => obj is IWindow other && Equals(other);

    public override int GetHashCode() => Handle.GetHashCode();
}
