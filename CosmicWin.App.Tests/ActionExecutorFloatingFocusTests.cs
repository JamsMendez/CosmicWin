using CosmicWin.App.Diagnostics;
using CosmicWin.App.Input;
using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;
using CosmicWin.Layout;

namespace CosmicWin.App.Tests;

/// <summary>
/// With the tray's tiling switch OFF, a focus chord chooses its target GEOMETRICALLY from the
/// foreground window's own rectangle, via <see cref="FloatingFocus.Toward"/>, instead of being
/// dropped the way every chord but resize used to be. See the docs on
/// <see cref="ActionExecutor.ResolveFloatingWindows"/> and the untiled branch of
/// <c>ActionExecutor.Execute</c> for the reasoning: a direction is a statement about where windows
/// ARE on screen, which stays true with no layout in force.
/// </summary>
public sealed class ActionExecutorFloatingFocusTests
{
    private static readonly nint ForegroundHandle = new(1);
    private static readonly nint TargetHandle = new(2);

    private static readonly Rectangle ForegroundBounds = Rectangle.FromSize(500, 500, 100, 100);
    private static readonly Rectangle TargetBounds = Rectangle.FromSize(700, 500, 100, 100);

    private sealed class FakeForegroundWindowSource : IForegroundWindowSource
    {
        public nint Handle { get; set; }

        public nint GetForegroundHandle() => Handle;
    }

    private sealed record Harness(
        ActionExecutor Executor, RecordingFocusTrace Trace, List<nint> Activated, Dictionary<nint, Rectangle> Bounds);

    /// <summary>
    /// Tiling off, one candidate straight ahead: the chord activates it and traces
    /// <see cref="FocusTraceOutcome.Activated"/>, exactly like the tiled path's own
    /// <c>Activated</c> line.
    /// </summary>
    [Fact]
    public async Task WithTilingOff_FocusChord_ActivatesTheGeometricallyCorrectWindow_AndTracesActivated()
    {
        var harness = Build();

        await harness.Executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.FocusRight), CancellationToken.None);

        Assert.Equal(TargetHandle, Assert.Single(harness.Activated));
        var entry = Assert.Single(harness.Trace.Entries);
        Assert.Equal(Direction.Right, entry.Direction);
        Assert.Equal(ForegroundHandle, entry.ForegroundHandle);
        Assert.Equal(TargetHandle, entry.TargetHandle);
        Assert.Equal(FocusTraceOutcome.Activated, entry.Outcome);
    }

    /// <summary>
    /// No <see cref="ActionExecutor.ResolveWindowBounds"/> answer for the foreground -- an
    /// unresolvable window -- traces <see cref="FocusTraceOutcome.UnresolvedFocus"/>, the same
    /// outcome the tiled path reports for a chord that never reached its walk.
    /// </summary>
    [Fact]
    public async Task WithTilingOff_UnresolvableForeground_TracesUnresolvedFocus()
    {
        var harness = Build();
        harness.Bounds.Remove(ForegroundHandle);

        await harness.Executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.FocusRight), CancellationToken.None);

        Assert.Empty(harness.Activated);
        var entry = Assert.Single(harness.Trace.Entries);
        Assert.Equal(FocusTraceOutcome.UnresolvedFocus, entry.Outcome);
        Assert.Equal(IntPtr.Zero, entry.FocusedHandle);
        Assert.Equal(IntPtr.Zero, entry.TargetHandle);
    }

    /// <summary>Nothing lies in the pressed direction: traces <see cref="FocusTraceOutcome.NoMatch"/>.</summary>
    [Fact]
    public async Task WithTilingOff_NothingInTheDirectionPressed_TracesNoMatch()
    {
        var harness = Build();

        await harness.Executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.FocusLeft), CancellationToken.None);

        Assert.Empty(harness.Activated);
        var entry = Assert.Single(harness.Trace.Entries);
        Assert.Equal(Direction.Left, entry.Direction);
        Assert.Equal(IntPtr.Zero, entry.TargetHandle);
        Assert.Equal(FocusTraceOutcome.NoMatch, entry.Outcome);
    }

    /// <summary>
    /// <see cref="ActionExecutor.ActivateUntrackedWindow"/> refusing the activation traces
    /// <see cref="FocusTraceOutcome.ActivateFailed"/>, never <c>NoMatch</c> -- the geometry found a
    /// real target, only the activation itself was refused.
    /// </summary>
    [Fact]
    public async Task WithTilingOff_ARefusedActivation_TracesActivateFailed()
    {
        var harness = Build(activationSucceeds: false);

        await harness.Executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.FocusRight), CancellationToken.None);

        var entry = Assert.Single(harness.Trace.Entries);
        Assert.Equal(TargetHandle, entry.TargetHandle);
        Assert.Equal(FocusTraceOutcome.ActivateFailed, entry.Outcome);
    }

    /// <summary>
    /// No regression: with <see cref="ActionExecutor.TilingEnabled"/> left at its default, the
    /// ordinary tree walk still runs, and the untiled geometric path -- which needs no
    /// <see cref="TreeManager"/> at all -- never gets a turn.
    /// </summary>
    [Fact]
    public async Task WithTilingLeftAtDefault_TheTiledTreeWalkStillRuns_NotTheFloatingPath()
    {
        var leafA = new LeafNode(new WindowRef(new IntPtr(1)));
        var leafB = new LeafNode(new WindowRef(new IntPtr(2)));
        var group = new GroupNode(SplitAxis.Horizontal) { GroupLength = 900 };
        group.Children.Add(leafA);
        group.Children.Add(leafB);
        group.Sizes.Add(450);
        group.Sizes.Add(450);
        leafA.Parent = group;
        leafB.Parent = group;

        ITilingEngine engine = new LayoutTree(group);
        var registry = new WindowRegistry();
        var windowA = new RecordingWindow(leafA.Window.Handle, Rectangle.Empty);
        var windowB = new RecordingWindow(leafB.Window.Handle, Rectangle.Empty);
        registry.Register(windowA, leafA);
        registry.Register(windowB, leafB);

        var foreground = new FakeForegroundWindowSource { Handle = windowA.Handle };
        var trace = new RecordingFocusTrace();
        var floatingWindowsAsked = false;
        var executor = new ActionExecutor(engine, registry, foreground)
        {
            WorkArea = new Rect(0, 0, 900, 100),
            FocusTrace = trace,
            // Wired but never called: proof the tiled walk answers the chord first and the
            // untiled path -- which reads this delegate -- never even runs.
            ResolveFloatingWindows = () =>
            {
                floatingWindowsAsked = true;
                return [];
            },
        };

        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.FocusRight), CancellationToken.None);

        Assert.False(floatingWindowsAsked);
        var entry = Assert.Single(trace.Entries);
        Assert.Equal(windowB.Handle, entry.TargetHandle);
        Assert.Equal(FocusTraceOutcome.Activated, entry.Outcome);
    }

    private static Harness Build(bool activationSucceeds = true)
    {
        var registry = new WindowRegistry();
        ITilingEngine engine = new LayoutTree();
        var foreground = new FakeForegroundWindowSource { Handle = ForegroundHandle };
        var trace = new RecordingFocusTrace();
        var activated = new List<nint>();

        var bounds = new Dictionary<nint, Rectangle>
        {
            [ForegroundHandle] = ForegroundBounds,
            [TargetHandle] = TargetBounds,
        };

        var executor = new ActionExecutor(engine, registry, foreground)
        {
            WorkArea = new Rect(0, 0, 1920, 1080),
            TilingEnabled = () => false,
            FocusTrace = trace,
            ResolveWindowBounds = handle => bounds.TryGetValue(handle, out var rect) ? rect : null,
            ResolveFloatingWindows = () =>
                bounds.Select(pair => (pair.Key, pair.Value)).ToList(),
            ActivateUntrackedWindow = handle =>
            {
                if (!activationSucceeds)
                {
                    return false;
                }

                activated.Add(handle);
                return true;
            },
        };

        return new Harness(executor, trace, activated, bounds);
    }
}
