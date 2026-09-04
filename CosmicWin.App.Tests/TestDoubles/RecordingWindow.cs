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

    /// <summary>
    /// Every position this window has been handed, in order.
    /// </summary>
    /// <remarks>
    /// <see cref="LastSetPosition"/> alone cannot see a layout that changed and changed back, which
    /// is exactly what untiling a window and re-admitting it looks like from a sibling: it is given
    /// the whole work area for an instant and its own half again straight afterwards. A mutation
    /// that untiled a window it should have kept survived the suite on that blind spot.
    /// </remarks>
    public List<Rectangle> Positions { get; } = [];

    public int TryActivateCallCount { get; private set; }

    public Rectangle? LastSetPosition { get; private set; }

    /// <summary>
    /// Where this window ends up REGARDLESS of what it was asked for -- a window that accepts the
    /// reposition and then puts itself somewhere else.
    /// </summary>
    /// <remarks>
    /// Not the same as <see cref="FailNextSetPosition"/>, and the difference is the whole point.
    /// A refusal flips <see cref="CanReposition"/> and the arranger evicts it at once. This one
    /// reports success every time, so nothing upstream can tell it apart from a window that
    /// complied -- which is exactly how a real one produced 31,759 reflows in two minutes without
    /// ever failing a single call.
    /// </remarks>
    public Rectangle? SnapsBackTo { get; set; }

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

        LastSetPosition = bounds;
        Positions.Add(bounds);

        // The ASK is recorded above whatever happens next, because a fighter accepts every ask.
        Bounds = SnapsBackTo ?? Clamped(bounds);
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

    /// <summary>
    /// A size this window will not go under, exactly as <c>WM_GETMINMAXINFO</c> makes a real one
    /// behave: the position and every dimension that meets the floor are honoured, and one that
    /// does not is silently raised.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT <see cref="SnapsBackTo"/>, and the difference is the whole point. That one
    /// lands somewhere unrelated to what was asked, which is a window fighting. This one obeys as
    /// far as it can and reports the one thing it cannot do, which is a window with a constraint.
    /// Both miss their tile and both do it reproducibly, so only the SHAPE of the miss separates
    /// them -- and that is what the adapter has to read.
    /// </remarks>
    public (int Width, int Height)? MinimumSize { get; set; }

    /// <summary>
    /// A size this window will not go OVER, the other half of what <c>WM_GETMINMAXINFO</c> reports.
    /// Measured on NVIDIA Broadcast, which clamps its height UP to 772 and DOWN to 1000 -- a window
    /// with a floor usually has a ceiling too, and a double that models only one of them cannot
    /// reproduce what the trace showed.
    /// </summary>
    public (int Width, int Height)? MaximumSize { get; set; }

    /// <summary>
    /// The position is always honoured and the size is pulled into range, which is what separates a
    /// constrained window from <see cref="SnapsBackTo"/>'s fighter: a fighter moves.
    /// </summary>
    private Rectangle Clamped(Rectangle asked)
    {
        var width = asked.Width;
        var height = asked.Height;

        if (MinimumSize is { } floor)
        {
            width = Math.Max(width, floor.Width);
            height = Math.Max(height, floor.Height);
        }

        if (MaximumSize is { } ceiling)
        {
            width = Math.Min(width, ceiling.Width);
            height = Math.Min(height, ceiling.Height);
        }

        return Rectangle.FromSize(asked.Left, asked.Top, width, height);
    }

    /// <summary>Makes the next <see cref="SetPosition"/> call fail (threat matrix precedent).</summary>
    public void FailNextSetPosition() => _failNextSetPosition = true;

    /// <summary>Makes the next <see cref="TryActivate"/> call fail without throwing.</summary>
    public void FailNextActivate() => _failNextActivate = true;

    /// <summary>How many times this window was ASKED to close.</summary>
    public int CloseAskCount { get; private set; }

    /// <summary>When set, the ask is delivered nowhere -- the shape of a window already gone.</summary>
    public bool RefuseCloseAsk { get; set; }

    /// <summary>
    /// Records the ask and reports it delivered. Deliberately leaves <see cref="IsAlive"/> alone:
    /// WM_CLOSE is a request an application may refuse, and a double that quietly closed itself
    /// would let a fact assert a removal the real contract never promises.
    /// </summary>
    public bool TryClose()
    {
        if (!IsAlive || RefuseCloseAsk)
        {
            return false;
        }

        CloseAskCount++;
        return true;
    }

    /// <summary>
    /// Simulates an OUT-OF-BAND move (a mouse drag), the way the real OS reports it: <see
    /// cref="Bounds"/> changes WITHOUT going through <see cref="SetPosition"/> and without
    /// incrementing <see cref="SetPositionCallCount"/> -- this is what the WM did NOT do, not what
    /// it did.
    /// </summary>
    public void SimulateExternalMove(Rectangle bounds) => Bounds = bounds;

    /// <summary>
    /// Sets <c>WS_MAXIMIZE</c> and fills <paramref name="workArea"/>, so a test can reproduce a
    /// real Aero Snap maximise -- one drag gesture that ends in MOVESIZEEND like any other -- and
    /// not merely a window that happens to be screen-sized.
    /// </summary>
    public void SimulateMaximize(Rectangle workArea)
    {
        Style |= 0x01000000u;
        Bounds = workArea;
    }

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
