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

    /// <summary>
    /// Called when one action throws, with the action and the exception. Null discards the detail
    /// but never the survival: the pump carries on either way.
    /// </summary>
    /// <remarks>
    /// Surviving silently would trade a dead window manager for an invisible one, and this
    /// repository has already paid twice for failures that left no trace.
    /// </remarks>
    public Action<HotkeyAction, Exception>? OnActionFailed { get; init; }

    /// <summary>
    /// Drains queued actions in order until the channel completes or dispatch is cancelled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One action's failure must never end the pump. It used to: only
    /// <see cref="OperationCanceledException"/> was caught, so any other throw unwound the
    /// <c>await foreach</c> and ended dispatch for good. The keyboard hook keeps running and keeps
    /// queueing, the tray icon stays put, the window manager LOOKS alive -- and not one chord is
    /// ever answered again, with nothing on screen to say why.
    /// </para>
    /// <para>
    /// Reachable rather than theoretical: the executor's desktop path walks trees through
    /// <c>TreeManager</c>, which throws on an unknown node type or an empty group, and it drives
    /// cross-process activation whose interop is only partly guarded. A dropped chord is a bad
    /// outcome; a window manager that stops answering the keyboard is a much worse one.
    /// </para>
    /// <para>
    /// Cancellation is deliberately NOT a failure. Disposal cancels mid-action by design, so
    /// reporting it would fire an error every time the user quits.
    /// </para>
    /// </remarks>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _dispose.Token);
        try
        {
            await foreach (var action in _channel.Reader.ReadAllAsync(linked.Token))
            {
                try
                {
                    await _scheduler.ScheduleAsync(action, linked.Token);
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    OnActionFailed?.Invoke(action, error);
                }
            }
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
