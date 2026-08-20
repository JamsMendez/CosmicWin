using CosmicWin.App.Input;
using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;
using CosmicWin.Layout;

namespace CosmicWin.App.Tests;

/// <summary>
/// Task 2.17: writing a <see cref="HotkeyAction"/> to the wired pipeline's dispatcher channel
/// reaches a recording <see cref="ITilingEngine"/> fake with the correct <see cref="Direction"/>
/// and dispatched kind, end-to-end through <see cref="ActionDispatcher"/> -&gt; <see
/// cref="ActionExecutor"/>, using a fake engine/registry/foreground -- no live desktop required.
/// Closes verify-report #21 finding C1: before <see cref="CompositionRoot"/> existed, nothing but
/// tests ever constructed a wired <see cref="ActionDispatcher"/>/<see cref="ActionExecutor"/> pair.
/// </summary>
public sealed class CompositionRootTests
{
    private static (RecordingTilingEngine Engine, ActionDispatcher Dispatcher) BuildWiredPipeline(nint focusedHandle)
    {
        var leaf = new LeafNode(new WindowRef(focusedHandle));
        var engine = new RecordingTilingEngine();
        var registry = new WindowRegistry();
        var window = new RecordingWindow(focusedHandle, Rectangle.Empty);
        registry.Register(window, leaf);
        var foreground = new StaticForegroundWindowSource(focusedHandle);

        var (dispatcher, _) = CompositionRoot.Build(engine, registry, foreground, workArea: default);
        return (engine, dispatcher);
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        return condition();
    }

    [Fact]
    public async Task WiredPipeline_MoveRight_ReachesRecordingEngine_WithDirectionRight()
    {
        var (engine, dispatcher) = BuildWiredPipeline(new IntPtr(101));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var runTask = dispatcher.RunAsync(cts.Token);

        Assert.True(dispatcher.Writer.TryWrite(new HotkeyAction(HotkeyActionKind.MoveRight)));

        var observed = await WaitUntil(() => engine.MoveNodeCallCount > 0, TimeSpan.FromSeconds(2));

        await dispatcher.DisposeAsync();
        await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(1)));

        Assert.True(observed);
        Assert.Equal(1, engine.MoveNodeCallCount);
        Assert.Equal(Direction.Right, engine.LastMoveDirection);
        Assert.Equal(0, engine.ResizeNodeCallCount);
    }

    [Fact]
    public async Task WiredPipeline_ResizeDown_ReachesRecordingEngine_WithDirectionDown()
    {
        var (engine, dispatcher) = BuildWiredPipeline(new IntPtr(202));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var runTask = dispatcher.RunAsync(cts.Token);

        Assert.True(dispatcher.Writer.TryWrite(new HotkeyAction(HotkeyActionKind.ResizeDown)));

        var observed = await WaitUntil(() => engine.ResizeNodeCallCount > 0, TimeSpan.FromSeconds(2));

        await dispatcher.DisposeAsync();
        await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(1)));

        Assert.True(observed);
        Assert.Equal(1, engine.ResizeNodeCallCount);
        Assert.Equal(Direction.Down, engine.LastResizeDirection);
        Assert.Equal(0, engine.MoveNodeCallCount);
    }

    /// <summary>
    /// Verify-report #21 CRITICAL C1: proves the composition pipeline actually assigns a
    /// non-zero, display-derived work area to the executor -- previously nothing in production
    /// ever did, so the default <c>Rect(0,0,0,0)</c> zeroed every window on the first
    /// Move/Toggle/Resize chord. Uses a fake <see cref="IDisplay"/> (see <see
    /// cref="WorkAreaResolverTests"/> for the resolver's own pure conversion tests); no live
    /// desktop required, consistent with the rest of this file.
    /// </summary>
    [Fact]
    public void Build_AssignsExecutorWorkArea_FromResolvedDisplayWorkArea_NonZero()
    {
        var display = new FakeDisplay(
            new IntPtr(1), Rectangle.FromSize(0, 0, 1920, 1080), Rectangle.FromSize(0, 0, 1920, 1040), 1.0, isPrimary: true);
        var resolvedWorkArea = WorkAreaResolver.Resolve(display);

        var (_, executor) = CompositionRoot.Build(
            new RecordingTilingEngine(), new WindowRegistry(), new StaticForegroundWindowSource(IntPtr.Zero), resolvedWorkArea);

        Assert.NotEqual(default, executor.WorkArea);
        Assert.Equal(new Rect(0, 0, 1920, 1040), executor.WorkArea);
    }

    private sealed class RecordingTilingEngine : ITilingEngine
    {
        public int MoveNodeCallCount { get; private set; }
        public Direction LastMoveDirection { get; private set; }
        public int ResizeNodeCallCount { get; private set; }
        public Direction LastResizeDirection { get; private set; }

        public FocusResult NextFocus(Direction direction, LeafNode focused) => FocusResult.NoMatch;

        public bool MoveNode(Direction direction, Node focused)
        {
            MoveNodeCallCount++;
            LastMoveDirection = direction;
            return false;
        }

        public bool ToggleAxis(Node focused) => false;

        public bool ResizeNode(Direction direction, Node focused, double step = LayoutTree.DefaultResizeStep)
        {
            ResizeNodeCallCount++;
            LastResizeDirection = direction;
            return false;
        }

        public IReadOnlyList<(WindowRef Window, Rect Bounds)> Arrange(Rect workArea) =>
            Array.Empty<(WindowRef, Rect)>();
    }

    private sealed class StaticForegroundWindowSource(nint handle) : IForegroundWindowSource
    {
        public nint GetForegroundHandle() => handle;
    }
}
