using CosmicWin.App.Input;

namespace CosmicWin.App.Tests.Input;

public sealed class ActionDispatcherTests
{
    [Fact]
    public async Task Queue_WhenOverCapacity_DrainsOnlyFirstThirtyTwoActionsInFifoOrder()
    {
        await using var dispatcher = new ActionDispatcher(new RecordingScheduler());

        for (var i = 0; i < 32; i++)
            Assert.True(dispatcher.Writer.TryWrite(new((HotkeyActionKind)i)));
        Assert.False(dispatcher.Writer.TryWrite(new((HotkeyActionKind)32)));
        dispatcher.Writer.Complete();

        var drained = new List<HotkeyAction>();
        await foreach (var action in dispatcher.Reader.ReadAllAsync()) drained.Add(action);

        Assert.Equal(32, drained.Count);
        Assert.Equal(Enumerable.Range(0, 32), drained.Select(action => (int)action.Kind));
    }

    [Fact]
    public async Task RunAsync_HandsAcceptedActionsToUiSchedulerInFifoOrder()
    {
        var scheduler = new RecordingScheduler();
        await using var dispatcher = new ActionDispatcher(scheduler);
        dispatcher.Writer.TryWrite(new(HotkeyActionKind.FocusLeft));
        dispatcher.Writer.TryWrite(new(HotkeyActionKind.ToggleOrientation));
        dispatcher.Writer.Complete();

        await dispatcher.RunAsync(CancellationToken.None);

        Assert.Equal(
            [HotkeyActionKind.FocusLeft, HotkeyActionKind.ToggleOrientation],
            scheduler.Actions.Select(action => action.Kind));
    }

    /// <summary>
    /// One chord that throws must not take every LATER chord with it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The worst failure this application has, and it was one unhandled exception away. The pump
    /// caught <see cref="OperationCanceledException"/> and nothing else, so any other throw from a
    /// scheduled action unwound the <c>await foreach</c> and ended <c>RunAsync</c> for good. The
    /// keyboard hook keeps running and keeps queueing, the tray icon stays put, the window manager
    /// looks alive -- and not one chord is ever answered again. Nothing on screen says why.
    /// </para>
    /// <para>
    /// Reachable rather than theoretical: the executor's desktop path walks trees through
    /// <c>TreeManager</c>, which throws <see cref="InvalidOperationException"/> on an unknown node
    /// type or an empty group, and it drives cross-process activation whose interop is only
    /// partly guarded.
    /// </para>
    /// <para>
    /// A dropped chord is a bad outcome and a dead window manager is a much worse one, so the pump
    /// survives the action rather than the other way round.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task RunAsync_WhenOneActionThrows_KeepsDispatchingTheRest()
    {
        var scheduler = new ThrowingScheduler(throwOn: HotkeyActionKind.ToggleOrientation);
        await using var dispatcher = new ActionDispatcher(scheduler);
        dispatcher.Writer.TryWrite(new(HotkeyActionKind.FocusLeft));
        dispatcher.Writer.TryWrite(new(HotkeyActionKind.ToggleOrientation));
        dispatcher.Writer.TryWrite(new(HotkeyActionKind.FocusRight));
        dispatcher.Writer.Complete();

        await dispatcher.RunAsync(CancellationToken.None);

        Assert.Equal(
            [HotkeyActionKind.FocusLeft, HotkeyActionKind.ToggleOrientation, HotkeyActionKind.FocusRight],
            scheduler.Seen.Select(action => action.Kind));
    }

    /// <summary>
    /// The failure is REPORTED, not merely survived.
    /// </summary>
    /// <remarks>
    /// Swallowing it silently trades a dead window manager for an invisible one, which this
    /// repository has already paid for twice: a manager whose failures leave no trace costs more to
    /// diagnose than the noise it saves.
    /// </remarks>
    [Fact]
    public async Task RunAsync_WhenAnActionThrows_ReportsWhichActionAndWhy()
    {
        var boom = new InvalidOperationException("Unknown node type");
        var scheduler = new ThrowingScheduler(HotkeyActionKind.ToggleOrientation, boom);
        var failures = new List<(HotkeyAction Action, Exception Error)>();
        await using var dispatcher = new ActionDispatcher(scheduler)
        {
            OnActionFailed = (action, error) => failures.Add((action, error)),
        };
        dispatcher.Writer.TryWrite(new(HotkeyActionKind.ToggleOrientation));
        dispatcher.Writer.Complete();

        await dispatcher.RunAsync(CancellationToken.None);

        var (action, error) = Assert.Single(failures);
        Assert.Equal(HotkeyActionKind.ToggleOrientation, action.Kind);
        Assert.Same(boom, error);
    }

    /// <summary>
    /// A CANCELLED dispatch is still a clean stop, not a failure to report.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Disposal cancels mid-action by design, and the pump must not start calling that an error --
    /// the shutdown path would then report one every time the user quits.
    /// </para>
    /// <para>
    /// The action really WAITS on the token rather than being handed a manufactured
    /// <see cref="OperationCanceledException"/>. A first version of this fact threw one without
    /// cancelling anything and went red, correctly: an <see cref="OperationCanceledException"/>
    /// raised while nothing has been cancelled is a spurious exception, not a shutdown, and the
    /// pump is right to report it. Only a token that actually fired means "we are stopping".
    /// </para>
    /// </remarks>
    [Fact]
    public async Task RunAsync_WhenCancelledMidAction_ReportsNoFailure()
    {
        var scheduler = new BlockingScheduler();
        var failures = new List<HotkeyAction>();
        var dispatcher = new ActionDispatcher(scheduler)
        {
            OnActionFailed = (action, _) => failures.Add(action),
        };
        dispatcher.Writer.TryWrite(new(HotkeyActionKind.ToggleOrientation));
        var running = dispatcher.RunAsync(CancellationToken.None);
        await scheduler.Entered.Task;

        await dispatcher.DisposeAsync();
        await running;

        Assert.Empty(failures);
    }

    /// <summary>Parks inside the action until the dispatch token is cancelled, the way a real shutdown does.</summary>
    private sealed class BlockingScheduler : IActionScheduler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask ScheduleAsync(HotkeyAction action, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    /// <summary>Throws for one chosen action kind and records every action it was handed.</summary>
    private sealed class ThrowingScheduler(HotkeyActionKind throwOn, Exception? error = null) : IActionScheduler
    {
        public List<HotkeyAction> Seen { get; } = [];

        public ValueTask ScheduleAsync(HotkeyAction action, CancellationToken cancellationToken)
        {
            Seen.Add(action);
            return action.Kind == throwOn
                ? throw (error ?? new InvalidOperationException("Unknown node type"))
                : ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task DisposeAsync_CancelsActiveDispatchAndCompletesWriter()
    {
        var scheduler = new RecordingScheduler();
        var dispatcher = new ActionDispatcher(scheduler);
        var running = dispatcher.RunAsync(CancellationToken.None);

        await dispatcher.DisposeAsync();

        await running;
        Assert.False(dispatcher.Writer.TryWrite(new(HotkeyActionKind.FocusLeft)));
    }

    [Fact]
    public async Task DisposeAsync_CanBeCalledMoreThanOnce()
    {
        var dispatcher = new ActionDispatcher(new RecordingScheduler());

        await dispatcher.DisposeAsync();

        await dispatcher.DisposeAsync();
        Assert.False(dispatcher.Writer.TryWrite(new(HotkeyActionKind.FocusLeft)));
    }

    private sealed class RecordingScheduler : IActionScheduler
    {
        public List<HotkeyAction> Actions { get; } = [];
        public ValueTask ScheduleAsync(HotkeyAction action, CancellationToken cancellationToken)
        {
            Actions.Add(action);
            return ValueTask.CompletedTask;
        }
    }
}
