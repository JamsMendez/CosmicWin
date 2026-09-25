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

    /// <summary>The raised default: most churn measured in the trace was this backstop firing on nothing but five quiet minutes.</summary>
    [Fact]
    public void DefaultWatchdogBackstop_IsThirtyMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(30), LowLevelKeyboardHook.DefaultWatchdogBackstop);
    }

    /// <summary>The interval is a cheap early-out, never an independent trigger: the backstop alone decides whether to reinstall.</summary>
    [Fact]
    public void Watchdog_DoesNotReinstallBeforeTheBackstopHasPassed()
    {
        var platform = new FakeKeyboardHookPlatform();
        var clock = new FakeClock();
        var callerThread = Environment.CurrentManagedThreadId;
        var channel = Channel.CreateUnbounded<HotkeyAction>();
        using var hook = new LowLevelKeyboardHook(
            channel.Writer, platform, TimeSpan.FromSeconds(5), () => clock.Value, TimeSpan.FromSeconds(5));

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
    /// stops moving, and the backstop eventually puts it back -- which is exactly what "the
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
            channel.Writer, platform, TimeSpan.FromSeconds(5), () => clock.Value, TimeSpan.FromSeconds(5));

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
    /// manager tearing down a healthy hook every backstop and opening a gap a keypress can
    /// fall into.
    /// </para>
    /// <para>
    /// `_lastActivity` moves only when a key arrives, so the watchdog's condition is "nobody has
    /// typed for the backstop" -- which is the resting state of every idle keyboard. It has no test
    /// of whether the hook is alive at all.
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
            Channel.CreateUnbounded<HotkeyAction>().Writer, platform, TimeSpan.FromSeconds(5),
            () => clock.Value, TimeSpan.FromSeconds(5));

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
            Channel.CreateUnbounded<HotkeyAction>().Writer, platform, TimeSpan.FromSeconds(5),
            () => clock.Value, TimeSpan.FromSeconds(5));

        hook.Start();
        clock.Advance(5000);
        Assert.True(platform.SecondInstall.Wait(TimeSpan.FromSeconds(2)));

        Assert.True(SpinWait.SpinUntil(() => hook.WatchdogFoundHookGone >= 1, TimeSpan.FromSeconds(2)));
    }

    /// <summary>
    /// A machine nobody is touching -- no key, no click, no wheel -- is not a broken hook, and the
    /// watchdog must leave it alone short of the backstop.
    /// </summary>
    /// <remarks>
    /// This used to be tested by simulating an idle SESSION reading. That reading, and the cursor
    /// sampling it was compared against, are gone: the gate now asks only "how long since a key
    /// reached this hook", so an idle machine needs no simulated evidence at all -- the default fake
    /// platform, which reports no key, already is one.
    /// </remarks>
    [Fact]
    public void Watchdog_WithNoKeyForLessThanTheBackstop_LeavesTheHookAlone()
    {
        var platform = new FakeKeyboardHookPlatform();
        var clock = new FakeClock();
        using var hook = new LowLevelKeyboardHook(
            Channel.CreateUnbounded<HotkeyAction>().Writer, platform, TimeSpan.FromSeconds(5),
            () => clock.Value, TimeSpan.FromMinutes(30));

        hook.Start();
        clock.Advance((long)TimeSpan.FromMinutes(30).TotalMilliseconds - 1);

        // Waited on the LOOP rather than on a clock: several passes past the deadline have run and
        // decided to do nothing, which a timeout would only have guessed at.
        var pumps = platform.PumpCount;
        Assert.True(SpinWait.SpinUntil(() => platform.PumpCount > pumps + 3, TimeSpan.FromSeconds(2)));

        Assert.Equal(1, platform.InstallCount);
        Assert.Equal(0, hook.WatchdogReinstalls);
    }

    /// <summary>
    /// The decision this whole feature is: the backstop is the ONLY trigger. No key for that long,
    /// with nothing else consulted, puts the hook back.
    /// </summary>
    /// <remarks>
    /// Measured 2026-09-25: comparing missed input against the session's latest input -- the gate
    /// this replaces -- read an ordinary wheel or click with a still cursor as a missed key, 129
    /// reinstalls in 35 minutes, `foundGone=0` on every one. `foundGone=0` on every reinstall ever
    /// traced, in fact, so trading early recovery for no false reinstalls is the accepted cost.
    /// </remarks>
    [Fact]
    public void Watchdog_WithNoKeyForAtLeastTheBackstop_PutsItBack()
    {
        var platform = new FakeKeyboardHookPlatform();
        var clock = new FakeClock();
        using var hook = new LowLevelKeyboardHook(
            Channel.CreateUnbounded<HotkeyAction>().Writer, platform, TimeSpan.FromSeconds(5),
            () => clock.Value, TimeSpan.FromSeconds(60));

        hook.Start();
        clock.Advance(60_000);

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
