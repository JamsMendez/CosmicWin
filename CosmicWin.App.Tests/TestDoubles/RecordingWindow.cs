using CosmicWin.Interop;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App.Tests.TestDoubles;

/// <summary>
/// Minimal in-memory <see cref="IWindow"/> used by App-layer tests to observe how many times
/// <see cref="SetPosition"/>/<see cref="TryActivate"/> were invoked, and to force either call to
/// fail without throwing. Mirrors the Interop.Tests <c>FakeWindow</c> shape without depending on
/// that internal test-only type across assemblies.
/// </summary>
internal sealed class RecordingWindow : IWindow
{
    /// <summary>Real WS_SYSMENU|WS_MAXIMIZEBOX|WS_MINIMIZEBOX bits -- regression-safety default so pre-existing facts don't read as auto-excluded once filtering wires up.</summary>
    private const uint TileableStyleDefault = 0x00080000u | 0x00010000u | 0x00020000u;

    private bool _failNextSetPosition;
    private bool _failNextActivate;

    public RecordingWindow(
        nint handle,
        Rectangle bounds,
        string className = "",
        string processName = "",
        uint style = TileableStyleDefault,
        uint exStyle = 0u,
        bool isOwned = false)
    {
        Handle = handle;
        Bounds = bounds;
        IsAlive = true;
        CanReposition = true;
        ClassName = className;
        ProcessName = processName;
        Style = style;
        ExStyle = exStyle;
        IsOwned = isOwned;
    }

    public nint Handle { get; }

    public string Title => IsAlive ? "Recording" : string.Empty;

    public Rectangle Bounds { get; private set; }

    public bool IsAlive { get; private set; }

    public bool CanReposition { get; private set; }

    public string ClassName { get; private set; }

    public string ProcessName { get; private set; }

    public uint Style { get; private set; }

    public uint ExStyle { get; private set; }

    public bool IsOwned { get; private set; }

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

    /// <summary>
    /// Which rung a successful activation reports. <see cref="ActivationOutcome.Direct"/> by
    /// default, so every fact written before the rung existed keeps meaning what it meant.
    /// </summary>
    public ActivationOutcome NextActivation { get; set; } = ActivationOutcome.Direct;

    /// <summary>
    /// When set, every activation appends this window's handle. Shared across the windows of one
    /// harness, it records the ORDER activations happened in -- which counts alone cannot answer,
    /// and which is the entire question for anything that walks a set of windows.
    /// </summary>
    public List<nint>? ActivationLog { get; set; }

    /// <summary>
    /// Counted HERE rather than in <see cref="TryActivate"/>, so one activation is one count no
    /// matter which of the two readings the caller asked for.
    /// </summary>
    public ActivationOutcome Activate()
    {
        TryActivateCallCount++;
        ActivationLog?.Add(Handle);
        if (!IsAlive)
        {
            return ActivationOutcome.Failed;
        }

        if (_failNextActivate)
        {
            _failNextActivate = false;
            return ActivationOutcome.Failed;
        }

        return NextActivation;
    }

    public bool TryActivate() => Activate().Confirmed();

    /// <summary>Makes the next <see cref="SetPosition"/> call fail (threat matrix precedent).</summary>
    public void FailNextSetPosition() => _failNextSetPosition = true;

    /// <summary>Makes the next <see cref="TryActivate"/> call fail without throwing.</summary>
    public void FailNextActivate() => _failNextActivate = true;

    /// <summary>
    /// Simulates an OUT-OF-BAND move (a mouse drag), the way the real OS reports it: <see
    /// cref="Bounds"/> changes WITHOUT going through <see cref="SetPosition"/> and without
    /// incrementing <see cref="SetPositionCallCount"/> -- this is what the WM did NOT do, not what
    /// it did.
    /// </summary>
    public void SimulateExternalMove(Rectangle bounds) => Bounds = bounds;

    /// <summary>
    /// Sets/clears <c>WS_MINIMIZE</c> and parks the window at Win32's canonical minimized spot,
    /// so a test can reproduce a real minimize/restore rather than only a move.
    /// </summary>
    public void SimulateMinimize()
    {
        Style |= WindowStyleFlags.Minimized;
        Bounds = Rectangle.FromSize(-32000, -32000, 160, 28);
    }

    public void SimulateRestore(Rectangle bounds)
    {
        Style &= ~WindowStyleFlags.Minimized;
        Bounds = bounds;
    }

    public void Kill()
    {
        IsAlive = false;
        Bounds = Rectangle.Empty;
    }

    public bool Equals(IWindow? other) => other is not null && Handle == other.Handle;

    public override bool Equals(object? obj) => obj is IWindow other && Equals(other);

    public override int GetHashCode() => Handle.GetHashCode();
}
