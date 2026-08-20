using System.Threading.Channels;

namespace CosmicWin.App.Input;

public interface IActionScheduler
{
    ValueTask ScheduleAsync(HotkeyAction action, CancellationToken cancellationToken);
}

public sealed class ActionDispatcher : IAsyncDisposable
{
    private readonly Channel<HotkeyAction> _channel;
    private readonly IActionScheduler _scheduler;
    private readonly CancellationTokenSource _dispose = new();
    private int _disposed;

    public ActionDispatcher(IActionScheduler scheduler)
    {
        _scheduler = scheduler;
        _channel = Channel.CreateBounded<HotkeyAction>(new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        });
    }

    public ChannelWriter<HotkeyAction> Writer => _channel.Writer;
    internal ChannelReader<HotkeyAction> Reader => _channel.Reader;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _dispose.Token);
        try
        {
            await foreach (var action in _channel.Reader.ReadAllAsync(linked.Token))
                await _scheduler.ScheduleAsync(action, linked.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return ValueTask.CompletedTask;

        _channel.Writer.TryComplete();
        _dispose.Cancel();
        _dispose.Dispose();
        return ValueTask.CompletedTask;
    }
}
