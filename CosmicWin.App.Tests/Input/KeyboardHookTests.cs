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
    public void Watchdog_DoesNotReinstallBeforeItsIntervalHasPassed()
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

    /// <summary>
    /// And of those reinstalls, how many found a hook that was ACTUALLY GONE.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The counter above says the watchdog fired 200 times in twenty minutes. It does not say
    /// whether it was ever needed once, and those are completely different findings: one is a
    /// window manager rescuing itself from a hook Windows keeps killing, the other is a window
    /// manager tearing down a healthy hook every five seconds and opening 200 gaps a keypress can
    /// fall into.
    /// </para>
    /// <para>
    /// `_lastActivity` moves only when a key arrives, so the watchdog's condition is "nobody has
    /// typed for five seconds" -- which is the resting state of every keyboard. It has no test of
    /// whether the hook is alive at all.
    /// </para>
    /// <para>
    /// UnhookWindowsHookEx is that test, and it is already being called. It succeeds on a handle
    /// Windows still holds and FAILS on one Windows has already removed, so its result at the
    /// moment of the reinstall says which of the two stories is true -- for free, on a call the
    /// watchdog makes anyway.
    /// </para>
    /// </remarks>
    [Fact]
    public void Watchdog_WhoseUnhookSucceeds_ReplacedAHookThatWasStillInstalled()
    {
        var platform = new FakeKeyboardHookPlatform { UninstallResult = true };
        var clock = new FakeClock();
        using var hook = new LowLevelKeyboardHook(
            Channel.CreateUnbounded<HotkeyAction>().Writer, platform, TimeSpan.FromSeconds(5), () => clock.Value);

        hook.Start();
        clock.Advance(5000);
        Assert.True(platform.SecondInstall.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(SpinWait.SpinUntil(() => hook.WatchdogReinstalls >= 1, TimeSpan.FromSeconds(2)));

        Assert.Equal(0, hook.WatchdogFoundHookGone);
    }

    /// <summary>
    /// The other half, and the only one that would justify the watchdog: a handle Windows had
    /// already thrown away.
    /// </summary>
    /// <remarks>
    /// Counted on the WATCHDOG path alone. The teardown in Dispose calls the same unhook, and a
    /// counter that included it would report one phantom rescue on every shutdown of a build whose
    /// hook was healthy the whole time.
    /// </remarks>
    [Fact]
    public void Watchdog_WhoseUnhookFails_ReplacedAHookWindowsHadAlreadyRemoved()
    {
        var platform = new FakeKeyboardHookPlatform { UninstallResult = false };
        var clock = new FakeClock();
        using var hook = new LowLevelKeyboardHook(
            Channel.CreateUnbounded<HotkeyAction>().Writer, platform, TimeSpan.FromSeconds(5), () => clock.Value);

        hook.Start();
        clock.Advance(5000);
        Assert.True(platform.SecondInstall.Wait(TimeSpan.FromSeconds(2)));

        Assert.True(SpinWait.SpinUntil(() => hook.WatchdogFoundHookGone >= 1, TimeSpan.FromSeconds(2)));
    }

    /// <summary>
    /// An idle machine is not a broken hook, and the watchdog must leave it alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured on hardware, nobody at the keyboard: 39 reinstalls in 200 seconds, `foundGone=0`
    /// on every one. The trigger was "no key has arrived for five seconds", which is the resting
    /// state of every keyboard on earth, so a healthy hook was torn down and put back roughly
    /// twelve times a minute for the life of the process -- and each teardown is a window a
    /// keypress can fall into, which is exactly what "it went dead, I pressed it again and it
    /// worked" looks like.
    /// </para>
    /// <para>
    /// Silence is not evidence. MISSED INPUT is: the session received something and this hook did
    /// not see it. GetLastInputInfo answers that without a hook, which is what makes it usable as
    /// evidence about one.
    /// </para>
    /// </remarks>
    [Fact]
    public void Watchdog_OnAnIdleMachine_LeavesTheHookAlone()
    {
        var platform = new FakeKeyboardHookPlatform { SystemInputAge = 60_000 };
        var clock = new FakeClock();
        using var hook = new LowLevelKeyboardHook(
            Channel.CreateUnbounded<HotkeyAction>().Writer, platform, TimeSpan.FromSeconds(5), () => clock.Value);

        hook.Start();
        clock.Advance(60_000);

        // Waited on the LOOP rather than on a clock: several passes past the deadline have run and
        // decided to do nothing, which a timeout would only have guessed at.
        var pumps = platform.PumpCount;
        Assert.True(SpinWait.SpinUntil(() => platform.PumpCount > pumps + 3, TimeSpan.FromSeconds(2)));

        Assert.Equal(1, platform.InstallCount);
        Assert.Equal(0, hook.WatchdogReinstalls);
    }

    /// <summary>
    /// The safety net still has a floor under it: a long enough silence puts the hook back
    /// regardless of what the session says.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Gating the watchdog on GetLastInputInfo makes the whole net depend on that one reading being
    /// truthful. If it ever under-reports -- and it already declines to count input this very
    /// process INJECTS, measured -- a genuinely dead hook would never be replaced, and the symptom
    /// is a keyboard that stays dead until the app is restarted. That is a worse failure than the
    /// churn being removed here.
    /// </para>
    /// <para>
    /// So the reading decides HOW OFTEN, not WHETHER. The backstop is sixty times the interval, so
    /// it costs a sixtieth of the teardowns the old trigger produced while keeping a guaranteed
    /// recovery for a reading that turns out to be wrong.
    /// </para>
    /// </remarks>
    [Fact]
    public void Watchdog_AfterALongEnoughSilence_PutsTheHookBackAnyway()
    {
        var platform = new FakeKeyboardHookPlatform { SystemInputAge = 600_000 };
        var clock = new FakeClock();
        using var hook = new LowLevelKeyboardHook(
            Channel.CreateUnbounded<HotkeyAction>().Writer, platform, TimeSpan.FromSeconds(5),
            () => clock.Value, TimeSpan.FromSeconds(300));

        hook.Start();
        clock.Advance(300_000);

        Assert.True(platform.SecondInstall.Wait(TimeSpan.FromSeconds(2)));
    }

    /// <summary>
    /// And the case it exists for: the session got input that never reached this hook.
    /// </summary>
    [Fact]
    public void Watchdog_WhenTheSystemSawInputThisHookDidNot_PutsItBack()
    {
        var platform = new FakeKeyboardHookPlatform { SystemInputAge = 500 };
        var clock = new FakeClock();
        using var hook = new LowLevelKeyboardHook(
            Channel.CreateUnbounded<HotkeyAction>().Writer, platform, TimeSpan.FromSeconds(5), () => clock.Value);

        hook.Start();
        clock.Advance(5000);

        Assert.True(platform.SecondInstall.Wait(TimeSpan.FromSeconds(2)));
    }

    /// <summary>
    /// A question the shell refuses is not an answer of "idle", so the net stays up.
    /// </summary>
    /// <remarks>
    /// GetLastInputInfo fails when the calling thread is not on the interactive desktop, and
    /// reading that as "nothing has happened" would silently retire the watchdog on exactly the
    /// machines nobody can look at. Unknown reinstalls, which is what this code did before the
    /// reading existed at all.
    /// </remarks>
    [Fact]
    public void Watchdog_WhenTheShellWillNotSay_PutsItBack()
    {
        var platform = new FakeKeyboardHookPlatform { RefuseSystemInputQuestion = true };
        var clock = new FakeClock();
        using var hook = new LowLevelKeyboardHook(
            Channel.CreateUnbounded<HotkeyAction>().Writer, platform, TimeSpan.FromSeconds(5), () => clock.Value);

        hook.Start();
        clock.Advance(5000);

        Assert.True(platform.SecondInstall.Wait(TimeSpan.FromSeconds(2)));
    }

    /// <summary>Shutting down is not a rescue: the unhook in Dispose never moves that counter.</summary>
    [Fact]
    public void Disposing_ADeadHook_IsNotCountedAsAWatchdogRescue()
    {
        var platform = new FakeKeyboardHookPlatform { UninstallResult = false };
        var hook = new LowLevelKeyboardHook(
            Channel.CreateUnbounded<HotkeyAction>().Writer, platform, TimeSpan.FromSeconds(5), () => 0);
        hook.Start();

        hook.Dispose();

        Assert.Equal(0, hook.WatchdogFoundHookGone);
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
