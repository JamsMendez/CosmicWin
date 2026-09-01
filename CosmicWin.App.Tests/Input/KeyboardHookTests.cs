using System.Threading.Channels;
using CosmicWin.App.Input;
using CosmicWin.App.Tests.TestDoubles;

namespace CosmicWin.App.Tests.Input;

public sealed class KeyboardHookTests
{
    [Fact]
    public void Process_ExactChord_SuppressesAndWritesOnlyMappedAction()
    {
        var channel = Channel.CreateBounded<HotkeyAction>(1);
        var processor = new KeyboardEventProcessor(ChordTable.Default);

        var suppressed = processor.Process(KeyboardKey.L, true, ModifierKeys.Alt, channel.Writer);

        Assert.True(suppressed);
        Assert.True(channel.Reader.TryRead(out var action));
        Assert.Equal(HotkeyActionKind.FocusRight, action.Kind);
    }

    [Theory]
    [InlineData(KeyboardKey.Menu, ModifierKeys.None)]
    [InlineData(KeyboardKey.Tab, ModifierKeys.Alt)]
    [InlineData(KeyboardKey.F4, ModifierKeys.Alt)]
    [InlineData(KeyboardKey.Space, ModifierKeys.Alt)]
    [InlineData(KeyboardKey.Escape, ModifierKeys.Alt)]
    [InlineData(KeyboardKey.Enter, ModifierKeys.Alt)]
    [InlineData(KeyboardKey.Delete, ModifierKeys.Control | ModifierKeys.Alt)]
    public void Process_NativeOrReservedChord_PassesThroughWithoutWriting(
        KeyboardKey key, ModifierKeys modifiers)
    {
        var channel = Channel.CreateBounded<HotkeyAction>(1);
        var processor = new KeyboardEventProcessor(ChordTable.Default);

        Assert.False(processor.Process(key, true, modifiers, channel.Writer));
        Assert.False(channel.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Process_SaturatedProductionDispatcher_DoesNotSuppressRejectedChord()
    {
        await using var dispatcher = new ActionDispatcher(new RecordingScheduler());
        for (var i = 0; i < 32; i++)
            Assert.True(dispatcher.Writer.TryWrite(new(HotkeyActionKind.FocusLeft)));
        var processor = new KeyboardEventProcessor(ChordTable.Default);

        var suppressed = processor.Process(KeyboardKey.L, true, ModifierKeys.Alt, dispatcher.Writer);

        Assert.False(suppressed);
        Assert.False(processor.Process(KeyboardKey.L, false, ModifierKeys.Alt, dispatcher.Writer));
        dispatcher.Writer.Complete();
        Assert.Equal(32, dispatcher.Reader.Count);
    }

    private sealed class RecordingScheduler : IActionScheduler
    {
        public ValueTask ScheduleAsync(HotkeyAction action, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    [Fact]
    public void Process_WarmedMatchedCallback_AllocatesNoManagedMemory()
    {
        var channel = Channel.CreateUnbounded<HotkeyAction>();
        var processor = new KeyboardEventProcessor(ChordTable.Default);
        processor.Process(KeyboardKey.H, true, ModifierKeys.Alt, channel.Writer);
        channel.Reader.TryRead(out _);
        var before = GC.GetAllocatedBytesForCurrentThread();

        processor.Process(KeyboardKey.H, true, ModifierKeys.Alt, channel.Writer);

        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    [Fact]
    public void Process_KeyUp_IsSuppressedOnlyAfterAcceptedKeyDown()
    {
        var channel = Channel.CreateBounded<HotkeyAction>(1);
        var processor = new KeyboardEventProcessor(ChordTable.Default);

        Assert.True(processor.Process(KeyboardKey.H, true, ModifierKeys.Alt, channel.Writer));
        Assert.True(processor.Process(KeyboardKey.H, false, ModifierKeys.Alt, channel.Writer));
        Assert.False(processor.Process(KeyboardKey.H, false, ModifierKeys.Alt, channel.Writer));
        Assert.Equal(1, channel.Reader.Count);
    }

    /// <summary>Pausar gates hotkey matching entirely -- a registered chord is neither suppressed nor written to the channel while paused.</summary>
    [Fact]
    public void Process_WhilePaused_NeverMatchesAnyChord_AndNeverWritesToChannel()
    {
        var channel = Channel.CreateBounded<HotkeyAction>(1);
        var processor = new KeyboardEventProcessor(ChordTable.Default) { IsPaused = true };

        var suppressed = processor.Process(KeyboardKey.H, true, ModifierKeys.Alt, channel.Writer);

        Assert.False(suppressed);
        Assert.False(channel.Reader.TryRead(out _));
    }

    /// <summary>Reanudar restores hotkeys identically to the never-paused baseline.</summary>
    [Fact]
    public void Process_AfterUnpause_MatchesChordsAgainIdenticallyToBaseline()
    {
        var channel = Channel.CreateBounded<HotkeyAction>(1);
        var processor = new KeyboardEventProcessor(ChordTable.Default) { IsPaused = true };
        processor.Process(KeyboardKey.L, true, ModifierKeys.Alt, channel.Writer);

        processor.IsPaused = false;
        var suppressed = processor.Process(KeyboardKey.L, true, ModifierKeys.Alt, channel.Writer);

        Assert.True(suppressed);
        Assert.True(channel.Reader.TryRead(out var action));
        Assert.Equal(HotkeyActionKind.FocusRight, action.Kind);
    }

    /// <summary>Smoke-level: <see cref="LowLevelKeyboardHook.IsPaused"/> is a pass-through onto the underlying processor -- the tray writes through the hook, never the processor directly.</summary>
    [Fact]
    public void IsPaused_OnLowLevelKeyboardHook_PassesThroughToUnderlyingProcessor()
    {
        var platform = new FakeKeyboardHookPlatform();
        using var hook = new LowLevelKeyboardHook(
            Channel.CreateUnbounded<HotkeyAction>().Writer, platform, TimeSpan.FromSeconds(5), () => 0);

        Assert.False(hook.IsPaused);
        hook.IsPaused = true;

        Assert.True(hook.IsPaused);
    }

    [Fact]
    public void Watchdog_ReinstallsOnlyAfterFiveSecondsWithoutKeyboardActivity()
    {
        var platform = new FakeKeyboardHookPlatform();
        var clock = new FakeClock();
        var callerThread = Environment.CurrentManagedThreadId;
        var channel = Channel.CreateUnbounded<HotkeyAction>();
        using var hook = new LowLevelKeyboardHook(
            channel.Writer, platform, TimeSpan.FromSeconds(5), () => clock.Value);

        hook.Start();
        clock.Advance(4000);
        platform.RaiseActivity();
        var pumps = platform.PumpCount;
        clock.Advance(1000);

        Assert.True(SpinWait.SpinUntil(() => platform.PumpCount > pumps, TimeSpan.FromSeconds(2)));
        Assert.Equal(1, platform.InstallCount);
        clock.Advance(4000);
        Assert.True(platform.SecondInstall.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(ApartmentState.STA, platform.ApartmentState);
        Assert.NotEqual(callerThread, platform.CallbackThreadId);
        Assert.True(channel.Reader.TryRead(out var action));
        Assert.Equal(HotkeyActionKind.FocusLeft, action.Kind);
        Assert.True(platform.UninstallCount >= 1);
    }

    /// <summary>
    /// Every watchdog reinstall is counted, because a reinstall is currently INVISIBLE.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Windows silently uninstalls a low-level keyboard hook whose callback overruns
    /// <c>LowLevelHooksTimeout</c>. A ghosted hook delivers no events, so <c>_lastActivity</c>
    /// stops moving, and five seconds later the watchdog puts it back -- which is exactly what "the
    /// chord went dead and I had to press the modifier again" would look like from the outside.
    /// </para>
    /// <para>
    /// That whole sequence writes nothing anywhere today. The reinstall leaves no record, so a
    /// dead stretch caused by a ghosted hook and a dead stretch caused by a lost modifier are
    /// indistinguishable in the trace -- and telling those two apart decides which defect is real.
    /// </para>
    /// <para>
    /// Counted in memory and published by the reconciliation tick, never written from here: this
    /// runs on the hook's own thread, and a hook that touches a file is a hook Windows uninstalls.
    /// </para>
    /// </remarks>
    [Fact]
    public void Watchdog_CountsEveryReinstall()
    {
        var platform = new FakeKeyboardHookPlatform();
        var clock = new FakeClock();
        var channel = Channel.CreateUnbounded<HotkeyAction>();
        using var hook = new LowLevelKeyboardHook(
            channel.Writer, platform, TimeSpan.FromSeconds(5), () => clock.Value);

        hook.Start();
        Assert.Equal(0, hook.WatchdogReinstalls);

        clock.Advance(5000);
        Assert.True(platform.SecondInstall.Wait(TimeSpan.FromSeconds(2)));

        Assert.True(SpinWait.SpinUntil(() => hook.WatchdogReinstalls >= 1, TimeSpan.FromSeconds(2)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Dispose_InvokesNativeUnhookOnceAndExposesItsResult(bool nativeResult)
    {
        var platform = new FakeKeyboardHookPlatform { UninstallResult = nativeResult };
        var hook = new LowLevelKeyboardHook(
            Channel.CreateUnbounded<HotkeyAction>().Writer, platform, TimeSpan.FromSeconds(5), () => 0);
        hook.Start();

        hook.Dispose();

        Assert.Equal(1, platform.UninstallCount);
        Assert.Equal(nativeResult, hook.UnhookSucceeded);
    }

    private sealed class FakeClock
    {
        public long Value => Interlocked.Read(ref _value);
        private long _value;
        public void Advance(long milliseconds) => Interlocked.Add(ref _value, milliseconds);
    }
}
