using CosmicWin.App.Diagnostics;
using CosmicWin.App.Input;
using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;
using CosmicWin.Layout;

namespace CosmicWin.App.Tests;

/// <summary>
/// MR-2 diagnosis (Engram discovery #101): `Alt+H`/`Alt+L` do nothing on real hardware even with
/// the `AttachThreadInput` activation fix in place, and the two remaining candidates -- `NextFocus`
/// never returning <see cref="FocusWalkStatus.Found"/>, versus <see cref="IWindow.TryActivate"/>
/// still failing -- produce the SAME external symptom (nothing happens). These tests pin the
/// per-keypress diagnostic that separates them, so one supervised run settles it instead of another
/// guess. The trace is scoped to focus chords only: Move/Resize already work and must stay silent.
/// </summary>
public sealed class ActionExecutorFocusTraceTests
{
    private static (ActionExecutor Executor, FakeForegroundWindowSource Foreground, WindowRegistry Registry,
        RecordingFocusTrace Trace, RecordingWindow WindowA, RecordingWindow WindowB) BuildTwoLeafRow()
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
        var executor = new ActionExecutor(engine, registry, foreground)
        {
            WorkArea = new Rect(0, 0, 900, 100),
            FocusTrace = trace
        };

        return (executor, foreground, registry, trace, windowA, windowB);
    }

    [Fact]
    public async Task FocusRight_WhenActivationSucceeds_RecordsFoundTargetAndActivated()
    {
        var (executor, _, _, trace, _, windowB) = BuildTwoLeafRow();

        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.FocusRight), CancellationToken.None);

        var entry = Assert.Single(trace.Entries);
        Assert.Equal(Direction.Right, entry.Direction);
        Assert.Equal(new IntPtr(1), entry.FocusedHandle);
        Assert.Equal(windowB.Handle, entry.TargetHandle);
        Assert.Equal(FocusTraceOutcome.Activated, entry.Outcome);
    }

    [Fact]
    public async Task FocusLeft_AtLeftBoundary_RecordsNoMatch_WithNoTargetHandle()
    {
        var (executor, _, _, trace, _, _) = BuildTwoLeafRow();

        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.FocusLeft), CancellationToken.None);

        var entry = Assert.Single(trace.Entries);
        Assert.Equal(Direction.Left, entry.Direction);
        Assert.Equal(new IntPtr(1), entry.FocusedHandle);
        Assert.Equal(IntPtr.Zero, entry.TargetHandle);
        Assert.Equal(FocusTraceOutcome.NoMatch, entry.Outcome);
    }

    [Fact]
    public async Task FocusRight_WhenTryActivateReturnsFalse_RecordsActivateFailed_NotNoMatch()
    {
        var (executor, _, _, trace, _, windowB) = BuildTwoLeafRow();
        windowB.FailNextActivate();

        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.FocusRight), CancellationToken.None);

        var entry = Assert.Single(trace.Entries);
        Assert.Equal(windowB.Handle, entry.TargetHandle);
        Assert.Equal(FocusTraceOutcome.ActivateFailed, entry.Outcome);
    }

    [Fact]
    public async Task FocusRight_WhenTargetLeafHasNoRegisteredWindow_RecordsUntrackedTarget()
    {
        var (executor, _, registry, trace, _, windowB) = BuildTwoLeafRow();
        registry.Remove(windowB.Handle);

        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.FocusRight), CancellationToken.None);

        var entry = Assert.Single(trace.Entries);
        Assert.Equal(windowB.Handle, entry.TargetHandle);
        Assert.Equal(FocusTraceOutcome.UntrackedTarget, entry.Outcome);
    }

    [Fact]
    public async Task FocusRight_WhenForegroundIsUntracked_RecordsUnresolvedFocus()
    {
        var (executor, foreground, _, trace, _, _) = BuildTwoLeafRow();
        foreground.Handle = new IntPtr(404);

        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.FocusRight), CancellationToken.None);

        var entry = Assert.Single(trace.Entries);
        Assert.Equal(Direction.Right, entry.Direction);
        Assert.Equal(IntPtr.Zero, entry.FocusedHandle);
        Assert.Equal(IntPtr.Zero, entry.TargetHandle);
        Assert.Equal(FocusTraceOutcome.UnresolvedFocus, entry.Outcome);
    }

    [Fact]
    public async Task MoveAndResizeChords_RecordNothing_TraceIsScopedToFocusOnly()
    {
        var (executor, _, _, trace, _, _) = BuildTwoLeafRow();

        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.MoveRight), CancellationToken.None);
        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.ResizeLeft), CancellationToken.None);
        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.ToggleOrientation), CancellationToken.None);

        Assert.Empty(trace.Entries);
    }

    [Fact]
    public async Task FocusRight_WithNoTraceAttached_StillNoOpsWithoutThrowing()
    {
        var (executor, _, _, _, _, _) = BuildTwoLeafRow();
        executor.FocusTrace = null;

        var exception = await Record.ExceptionAsync(
            () => executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.FocusRight), CancellationToken.None).AsTask());

        Assert.Null(exception);
    }

    private sealed class FakeForegroundWindowSource : IForegroundWindowSource
    {
        public nint Handle { get; set; }

        public nint GetForegroundHandle() => Handle;
    }
}
