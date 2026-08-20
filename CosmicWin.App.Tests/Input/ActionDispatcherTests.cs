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
