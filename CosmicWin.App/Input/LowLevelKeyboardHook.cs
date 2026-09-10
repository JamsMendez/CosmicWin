using System.Threading.Channels;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CosmicWin.App.Input;

public sealed class KeyboardEventProcessor(ChordTable chords)
{
    private readonly bool[] _acceptedKeyDown = new bool[256];
    private volatile bool _paused;

    /// <summary>The failure currently repeating, and how many times running it has done so.</summary>
    /// <remarks>
    /// Touched only from the hook's own thread, which is the only caller of <see cref="Process"/>.
    /// The cross-thread value is <see cref="LastUnmatched"/>, and it is published as one finished
    /// string precisely so a reader can never catch the text and the count a beat apart.
    /// </remarks>
    private string? _repeating;
    private int _repeats;

    /// <summary>The last modifier+key combination that matched no chord, for diagnosis. Never null-cleared.</summary>
    public volatile string? LastUnmatched;

    /// <summary>
    /// How many matched chords <see cref="Process"/> discarded because the dispatcher channel was
    /// full. Touched only from <see cref="RecordDropped"/>, on the hook's own thread; read from
    /// anywhere with <see cref="Volatile.Read(ref int)"/>, matching the discipline already used for
    /// the watchdog counters on <see cref="LowLevelKeyboardHook"/>.
    /// </summary>
    private int _dropped;

    /// <summary>See <see cref="_dropped"/>.</summary>
    public int Dropped => Volatile.Read(ref _dropped);

    /// <summary>The last dropped chord's description, in the form <c>{modifiers}+{key}</c>. Never null-cleared, mirroring <see cref="LastUnmatched"/>.</summary>
    public volatile string? LastDropped;

    /// <summary>
    /// The modifier keys PHYSICALLY down, by side, as a short string -- empty when none are.
    /// Unset leaves the diagnosis exactly as narrow as it was before it existed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Injected rather than read here, and not only for testability: reading it is Win32, and this
    /// type is otherwise free of it. <see cref="WindowsKeyboardHookPlatform"/> supplies the real
    /// one.
    /// </para>
    /// <para>
    /// It exists because <see cref="ModifierKeys"/> is a CONCLUSION, and the reported defect is
    /// about that conclusion being wrong: "the right Alt sometimes does not switch desktops, the
    /// left Alt always does" outlived two measured hypotheses because the trace only ever showed
    /// what was concluded, never what was held. Computed Alt against raw RMENU says the conclusion
    /// is right and the fault is further down; computed None against raw RMENU says the conclusion
    /// lost it; computed None against nothing held says the key state was already gone.
    /// </para>
    /// </remarks>
    public Func<string>? PhysicalModifiers { get; init; }

    /// <summary>While <c>true</c>, no chord matches and nothing is written to the dispatcher channel. Written from the tray's UI thread, read from this hook's dedicated STA thread -- <c>volatile</c> mirrors the existing <see cref="LowLevelKeyboardHook"/> <c>_lastActivity</c> pattern.</summary>
    public bool IsPaused
    {
        get => _paused;
        set => _paused = value;
    }

    public bool Process(
        KeyboardKey key, bool isKeyDown, ModifierKeys modifiers,
        ChannelWriter<HotkeyAction> writer)
    {
        var keyIndex = (byte)key;
        if (!isKeyDown)
        {
            var suppress = _acceptedKeyDown[keyIndex];
            _acceptedKeyDown[keyIndex] = false;
            return suppress;
        }
        if (_paused) return false;
        if (!chords.TryMatch(modifiers, key, out var action))
        {
            // A chord that matches nothing vanishes without trace, which is indistinguishable from
            // a broken feature -- exactly how "the right Alt does not work" was reported, with no
            // way to see what modifiers actually arrived. Recorded in MEMORY only: this runs inside
            // the low-level keyboard hook, and Windows uninstalls a hook that takes too long.
            RecordUnmatched(key, modifiers);
            return false;
        }

        var accepted = writer.TryWrite(action);
        if (accepted)
        {
            // The run ends here. The count answers "how many times in a row did THIS chord fail", and
            // a chord that worked in between means the user was not staring at a dead keyboard. Only
            // the run is reset -- LastUnmatched itself is never null-cleared, so the last real failure
            // stays readable for as long as the app runs.
            //
            // Gated on ACCEPTANCE, deliberately. A chord that matched but was then dropped below is
            // not the keyboard coming back to life -- it is the same failure the user is living
            // through, wearing a different cause. Resetting the run for it would under-report exactly
            // the dead stretch this diagnosis exists to measure.
            _repeating = null;
            _repeats = 0;
        }
        else
        {
            // TryWrite on a channel with FullMode.Wait does NOT block -- it returns false, and the
            // chord that DID match vanishes with nothing to show for it. RecordUnmatched cannot see
            // this: the chord matched, so its path never runs.
            RecordDropped(key, modifiers);
        }

        return _acceptedKeyDown[keyIndex] = accepted;
    }

    /// <summary>
    /// Records a matched chord that <see cref="ChannelWriter{T}.TryWrite"/> discarded because the
    /// dispatcher channel was full.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Channel.CreateBounded</c> with <c>FullMode.Wait</c> reads as "this write blocks until
    /// there is room", and <c>TryWrite</c> is the one member on that same channel that never blocks
    /// at all -- on a full channel it returns <c>false</c> immediately, and the action that would
    /// have run vanishes with nothing to show for it. <see cref="RecordUnmatched"/> cannot see this
    /// failure: it runs only when <see cref="ChordTable.TryMatch"/> returns <c>false</c>, and a
    /// dropped chord is one <c>TryMatch</c> already said yes to.
    /// </para>
    /// <para>
    /// PRIVACY NOTE: <see cref="RecordUnmatched"/> deliberately refuses to record when nothing is
    /// held, because it would otherwise write the user's own typing -- passwords included -- to a
    /// file that lives for as long as the app runs. That floor does NOT apply here, and the reason
    /// is worth stating: a dropped chord is by definition one that MATCHED the chord table, so it is
    /// always one of our own registered combinations and never ordinary typing.
    /// </para>
    /// <para>
    /// Deliberately its own counter and its own last-description, never the
    /// <see cref="Publish(string)"/>/<see cref="_repeating"/>/<see cref="_repeats"/> machinery below
    /// -- that machinery answers "how many times running did THIS chord fail to match", and a
    /// dropped chord matched.
    /// </para>
    /// </remarks>
    private void RecordDropped(KeyboardKey key, ModifierKeys modifiers)
    {
        Interlocked.Increment(ref _dropped);
        LastDropped = $"{modifiers}+{key}";
    }

    /// <summary>
    /// Records an unmatched PRESS, and refuses to record anything the user was merely typing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The floor is privacy, not volume. <c>unmatched chord</c> reaches the desktop trace whether
    /// or not the <c>trace-dialogs</c> marker exists -- unlike the dialog paths, which are behind
    /// it -- so a record taken with nothing held would write the user's own typing, passwords
    /// included, to a file on disk for as long as the app runs. Requiring a modifier to be
    /// physically down costs nothing a failing chord has (every one of them is a modifier plus a
    /// key) and excludes everything ordinary typing is.
    /// </para>
    /// <para>
    /// With no snapshot wired the original rule stands unchanged, so nothing written before this
    /// existed starts recording more than it did.
    /// </para>
    /// </remarks>
    private void RecordUnmatched(KeyboardKey key, ModifierKeys modifiers)
    {
        if (PhysicalModifiers?.Invoke() is not { } raw)
        {
            if (modifiers != ModifierKeys.None)
            {
                Publish($"{modifiers}+{key}");
            }

            return;
        }

        // Nothing concluded AND nothing held is a plain keystroke, and none of this diagnosis's
        // business.
        if (modifiers == ModifierKeys.None && raw.Length == 0)
        {
            return;
        }

        Publish($"{modifiers}+{key} raw=[{raw}]");
    }

    /// <summary>
    /// Publishes one failure, with a repeat count once the same one happens twice running.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The count exists because the publisher writes only when the text CHANGES, and identical
    /// failures therefore collapsed into a single line. Measured on real hardware: the desktop
    /// chords went dead for 3.6 and 5.5 seconds and each dead stretch produced exactly ONE
    /// <c>unmatched chord: Alt+165 raw=[RALT]</c> line -- one lost chord and three hundred were the
    /// same picture, and which of the two it was is the entire diagnosis.
    /// </para>
    /// <para>
    /// Carried in the text rather than in a second property so the publisher needs no change and
    /// there is no second value to read out of step with this one. The first occurrence carries no
    /// suffix, so a chord that genuinely failed once reads exactly as it did before.
    /// </para>
    /// </remarks>
    private void Publish(string description)
    {
        _repeats = description == _repeating ? _repeats + 1 : 1;
        _repeating = description;
        LastUnmatched = _repeats == 1 ? description : $"{description} x{_repeats}";
    }
}

internal delegate bool KeyboardHookCallback(KeyboardKey key, bool isKeyDown, ModifierKeys modifiers);

internal interface IKeyboardHookPlatform
{
    void Install(KeyboardHookCallback callback);
    bool Uninstall();
    void PumpMessages();

    /// <summary>
    /// How long ago the SESSION last received any input, or <c>null</c> when the shell will not
    /// say. Answered without a hook, which is what makes it usable as evidence ABOUT the hook.
    /// </summary>
    long? MillisecondsSinceSystemInput();

    /// <summary>
    /// Where the cursor is right now, or <c>null</c> when the shell will not say. Answered without
    /// a hook, exactly like <see cref="MillisecondsSinceSystemInput"/>, which is what makes it
    /// usable as evidence about whether missed input could have been a KEY rather than the mouse.
    /// </summary>
    (int X, int Y)? CursorPosition();
}

public sealed class LowLevelKeyboardHook : IDisposable
{
    public static readonly TimeSpan DefaultWatchdogInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The silence after which the hook is replaced regardless of what the session reports.
    /// </summary>
    /// <remarks>
    /// Sixty times the interval, deliberately. Gating on GetLastInputInfo makes the whole net
    /// depend on that one reading being truthful, and if it ever under-reports, a dead hook would
    /// never be replaced and the keyboard would stay dead until a restart -- a worse failure than
    /// the churn being removed. So the reading decides how OFTEN, and this decides that it happens
    /// at all. The reading has since been measured to be truthful about injected input as well as
    /// physical, which makes this insurance rather than a known-necessary correction; it is kept
    /// because one API deciding whether the keyboard ever recovers is a bet worth not taking.
    /// </remarks>
    public static readonly TimeSpan DefaultWatchdogBackstop = TimeSpan.FromMinutes(5);
    private readonly ChannelWriter<HotkeyAction> _writer;
    private readonly IKeyboardHookPlatform _platform;
    private readonly TimeSpan _watchdogInterval;
    private readonly TimeSpan _watchdogBackstop;
    private readonly Func<long> _clock;
    private readonly CancellationTokenSource _stop = new();
    private readonly ManualResetEventSlim _started = new();
    private readonly KeyboardEventProcessor _processor = new(ChordTable.Default)
    {
        // Wired, or the diagnosis is a test that passes and an instrument that reads nothing.
        PhysicalModifiers = WindowsKeyboardHookPlatform.PhysicalModifierSides,
    };
    private Thread? _thread;
    private Exception? _startupFailure;
    private long _lastActivity;
    private int _watchdogReinstalls;
    private int _watchdogFoundHookGone;

    /// <summary>The cursor as of the last <see cref="SampleCursor"/> call, or <c>null</c> before the first sample. Touched only from the hook's own thread.</summary>
    private (int X, int Y)? _lastCursor;

    /// <summary><see cref="_clock"/> reading at the last cursor MOVE (never the first sample, which has nothing to compare against). Read from <see cref="ShouldReinstall"/> with <see cref="Volatile"/>, matching <see cref="_lastActivity"/>'s discipline.</summary>
    private long _lastCursorMove;

    public bool? UnhookSucceeded { get; private set; }

    /// <summary>How many times the watchdog has put the hook back.</summary>
    public int WatchdogReinstalls => Volatile.Read(ref _watchdogReinstalls);

    /// <summary>
    /// How many of those reinstalls found the hook ALREADY GONE, which is the only kind that
    /// rescued anything.
    /// </summary>
    /// <remarks>
    /// <see cref="WatchdogReinstalls"/> alone cannot tell the two stories apart. The watchdog's
    /// condition is that no key has arrived for its interval -- the resting state of every
    /// keyboard -- and it makes no test of whether the hook is alive. UnhookWindowsHookEx IS that
    /// test, and the watchdog already calls it: it succeeds on a handle Windows still holds and
    /// fails on one Windows has already removed. Counted on the watchdog path only, never on the
    /// teardown in <see cref="Dispose"/>.
    /// </remarks>
    public int WatchdogFoundHookGone => Volatile.Read(ref _watchdogFoundHookGone);

    /// <summary>Pass-through onto <see cref="_processor"/> -- the tray writes through the hook, never the processor directly.</summary>
    public bool IsPaused
    {
        get => _processor.IsPaused;
        set => _processor.IsPaused = value;
    }

    /// <summary>The last chord that matched nothing, so a "this key does nothing" report can be answered with what actually arrived.</summary>
    public string? LastUnmatchedChord => _processor.LastUnmatched;

    /// <summary>How many matched chords were discarded because the dispatcher channel was full.</summary>
    public int DroppedChords => _processor.Dropped;

    /// <summary>The last matched chord dropped for a full channel, so a dead stretch caused by this can be told apart from one caused by a lost modifier.</summary>
    public string? LastDroppedChord => _processor.LastDropped;

    public LowLevelKeyboardHook(ChannelWriter<HotkeyAction> writer)
        : this(writer, new WindowsKeyboardHookPlatform(), DefaultWatchdogInterval) { }

    internal LowLevelKeyboardHook(
        ChannelWriter<HotkeyAction> writer, IKeyboardHookPlatform platform,
        TimeSpan watchdogInterval, Func<long>? clock = null, TimeSpan? watchdogBackstop = null)
    {
        _writer = writer;
        _platform = platform;
        _watchdogInterval = watchdogInterval;
        _watchdogBackstop = watchdogBackstop ?? DefaultWatchdogBackstop;
        _clock = clock ?? (() => Environment.TickCount64);
    }

    public void Start()
    {
        if (_thread is not null) throw new InvalidOperationException("The hook has already started.");
        _thread = new Thread(Run) { IsBackground = true, Name = "CosmicWin keyboard hook" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!_started.Wait(TimeSpan.FromSeconds(2)))
            throw new TimeoutException("The keyboard hook thread did not start within two seconds.");
        if (_startupFailure is not null)
            throw new InvalidOperationException("The keyboard hook could not be installed.", _startupFailure);
    }

    private void Run()
    {
        try
        {
            _lastActivity = _clock();
            _lastCursorMove = _lastActivity;
            _platform.Install(OnKeyboardEvent);
            _started.Set();
            while (!_stop.IsCancellationRequested)
            {
                _platform.PumpMessages();
                SampleCursor();
                if (ShouldReinstall())
                {
                    // The result is the diagnosis. This unhook succeeds on a handle Windows still
                    // holds and fails on one Windows has already thrown away, so it says whether
                    // the hook being replaced was dead -- and therefore whether the watchdog has
                    // ever rescued anything, or has only ever been opening gaps in a healthy hook.
                    var wasStillInstalled = RecordUninstall();
                    _platform.Install(OnKeyboardEvent);
                    Volatile.Write(ref _lastActivity, _clock());

                    // Counted here and published by the reconciliation tick, never written from
                    // this thread: a hook that touches a file is a hook Windows uninstalls, which
                    // is the very failure this counter exists to make visible.
                    Interlocked.Increment(ref _watchdogReinstalls);
                    if (!wasStillInstalled)
                    {
                        Interlocked.Increment(ref _watchdogFoundHookGone);
                    }
                }
                _stop.Token.WaitHandle.WaitOne(5);
            }
        }
        catch (Exception exception)
        {
            _startupFailure = exception;
            _started.Set();
        }
        finally
        {
            RecordUninstall();
        }
    }

    /// <summary>
    /// Records where the cursor is, and when it last MOVED.
    /// </summary>
    /// <remarks>
    /// The FIRST sample only records the position -- there is nothing yet to compare it against, so
    /// it must not count as a move, or every hook would start out looking like the mouse just
    /// jumped to wherever it happened to be sitting.
    /// </remarks>
    private void SampleCursor()
    {
        if (_platform.CursorPosition() is not { } position) return;
        if (_lastCursor is not { } previous)
        {
            _lastCursor = position;
            return;
        }
        if (previous == position) return;
        _lastCursor = position;
        Volatile.Write(ref _lastCursorMove, _clock());
    }

    /// <summary>
    /// Whether the hook looks GONE, which is not the same question as whether anyone has typed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to ask only the second one, and a keyboard's resting state is silence. Measured
    /// on hardware with nobody sitting at it: 39 teardowns of a live hook in 200 seconds, every one
    /// reporting the handle Windows still held. Each teardown is a gap a keypress can fall into --
    /// the shape of "the chord went dead and worked when I pressed it again" -- so the watchdog was
    /// manufacturing the failure mode it was written to survive.
    /// </para>
    /// <para>
    /// Silence proves nothing; MISSED INPUT proves something. A hook Windows has removed stops
    /// seeing everything, so the session receiving input this hook did not see is the signature,
    /// and GetLastInputInfo reports the session's last input without needing a hook at all -- the
    /// one reading still available to a hook that may be dead.
    /// </para>
    /// <para>
    /// The comparison is between two AGES on the same clock, and "the session has been quiet at
    /// least as long as we have" is the skip. It is deliberately generous at the boundary: a real
    /// keypress reaches GetLastInputInfo a hair before it reaches this callback, so an alive hook
    /// always reads as slightly YOUNGER than the session and can never trip the test.
    /// </para>
    /// <para>
    /// MEASURED AND CORRECTED: GetLastInputInfo counts mouse input, and a low-level KEYBOARD hook
    /// does not, so a person who moves the mouse without typing held `sessionAge` near zero while
    /// `ourAge` grew without bound. Mouse-only injected input reinstalled a live hook every ~4.9
    /// seconds (12.2/min) -- 72 times in one session -- three times the 4.34/min average measured
    /// over seven days of logs, which totalled 8269 reinstalls with `foundGone=0` on every single
    /// one. The watchdog has never once found a hook that was actually gone.
    /// </para>
    /// <para>
    /// The correction: missed input is evidence of a dead KEYBOARD hook only if the input could
    /// have been a KEY. A cursor that has moved since this hook last saw a key names the mouse as
    /// the source, and that reading needs no hook at all -- <see cref="SampleCursor"/> takes it on
    /// every loop pass, exactly like <see cref="OnKeyboardEvent"/> takes <see cref="_lastActivity"/>
    /// on every key. So a stale <see cref="_lastCursorMove"/> -- no later than the last key this
    /// hook saw -- is what a keypress, and only a keypress, looks like.
    /// </para>
    /// <para>
    /// The REMAINING imprecision, stated rather than hidden: a click or a scroll wheel with a still
    /// cursor still reads as keyboard-shaped and will reinstall. That residue is far smaller than
    /// cursor movement, which was 100% of the churn measured, and closing it would need a mouse hook
    /// or raw input registration -- more machinery than the residue is worth.
    /// </para>
    /// <para>
    /// Fast recovery is preserved for the case that matters: a genuinely dead hook while someone is
    /// TYPING leaves the cursor still, so the gate still reinstalls within one interval.
    /// </para>
    /// <para>
    /// A refusal is not an idle machine. GetLastInputInfo fails off the interactive desktop, and
    /// reading that as "nothing happened" would retire the watchdog exactly where nobody can watch
    /// it, so an unanswered question reinstalls -- and the same holds for a refused cursor reading.
    /// </para>
    /// </remarks>
    private bool ShouldReinstall()
    {
        var lastActivity = Volatile.Read(ref _lastActivity);
        var ourAge = _clock() - lastActivity;
        if (ourAge < _watchdogInterval.TotalMilliseconds)
        {
            return false;
        }

        // Checked before the reading is consulted, so a reading that is silently wrong delays the
        // recovery instead of cancelling it.
        if (ourAge >= _watchdogBackstop.TotalMilliseconds)
        {
            return true;
        }

        if (_platform.MillisecondsSinceSystemInput() is not { } sessionAge) return true;
        if (sessionAge >= ourAge) return false;

        // The session saw something this hook did not, but only a KEY could mean the hook is dead --
        // a cursor that has moved since our last key names the mouse as the source instead.
        return Volatile.Read(ref _lastCursorMove) <= lastActivity;
    }

    private bool OnKeyboardEvent(KeyboardKey key, bool isKeyDown, ModifierKeys modifiers)
    {
        Volatile.Write(ref _lastActivity, _clock());
        return _processor.Process(key, isKeyDown, modifiers, _writer);
    }

    /// <summary>
    /// Unhooks and folds the result into <see cref="UnhookSucceeded"/>, reporting it as well.
    /// </summary>
    /// <remarks>
    /// <see cref="UnhookSucceeded"/> is a sticky AND over the process lifetime, which answers "did
    /// anything ever go wrong" and cannot answer "did THIS one". The watchdog needs the second
    /// question, once per reinstall.
    /// </remarks>
    private bool RecordUninstall()
    {
        var succeeded = _platform.Uninstall();
        UnhookSucceeded = (UnhookSucceeded ?? true) && succeeded;
        return succeeded;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _started.Dispose();
        _stop.Dispose();
    }
}

internal sealed unsafe class WindowsKeyboardHookPlatform : IKeyboardHookPlatform
{
    private HOOKPROC? _nativeCallback;
    private KeyboardHookCallback? _callback;
    private HHOOK _hook;

    public void Install(KeyboardHookCallback callback)
    {
        _callback = callback;
        _nativeCallback ??= HookProc;
        _hook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, _nativeCallback, HINSTANCE.Null, 0);
        if (_hook.IsNull) throw new InvalidOperationException("SetWindowsHookEx failed.");
    }

    public bool Uninstall()
    {
        if (_hook.IsNull) return true;
        var succeeded = PInvoke.UnhookWindowsHookEx(_hook);
        _hook = HHOOK.Null;
        return succeeded;
    }

    /// <summary>
    /// Answered through <c>GetLastInputInfo</c>, which reports the session's last input WITHOUT a
    /// hook of any kind -- the one reading available to a hook that may be dead.
    /// </summary>
    /// <remarks>
    /// Returned as an AGE rather than a timestamp on purpose. <c>dwTime</c> shares the 32-bit
    /// <c>GetTickCount</c> timeline, which wraps roughly every forty-nine days, and an age computed
    /// by unsigned subtraction is right across the wrap while a timestamp compared against a 64-bit
    /// clock is not.
    /// </remarks>
    public long? MillisecondsSinceSystemInput()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)sizeof(LASTINPUTINFO) };
        if (!PInvoke.GetLastInputInfo(ref info))
        {
            return null;
        }

        return unchecked(PInvoke.GetTickCount() - info.dwTime);
    }

    /// <summary>
    /// Answered through <c>GetCursorPos</c>, which -- like <see cref="MillisecondsSinceSystemInput"/>
    /// -- reports without needing a keyboard hook of any kind.
    /// </summary>
    public (int X, int Y)? CursorPosition()
    {
        if (!PInvoke.GetCursorPos(out var point))
        {
            return null;
        }

        return (point.X, point.Y);
    }

    public void PumpMessages()
    {
        while (PInvoke.PeekMessage(out var message, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE))
        {
            PInvoke.TranslateMessage(message);
            PInvoke.DispatchMessage(message);
        }
    }

    private LRESULT HookProc(int code, WPARAM wParam, LPARAM lParam)
    {
        if (code >= 0)
        {
            var message = (uint)wParam.Value;
            var isDown = message is PInvoke.WM_KEYDOWN or PInvoke.WM_SYSKEYDOWN;
            var isUp = message is PInvoke.WM_KEYUP or PInvoke.WM_SYSKEYUP;
            if (isDown || isUp)
            {
                var data = *(KBDLLHOOKSTRUCT*)lParam.Value;
                var modifiers = CurrentModifiers();
                if (_callback!((KeyboardKey)data.vkCode, isDown, modifiers)) return new LRESULT(1);
            }
        }
        return PInvoke.CallNextHookEx(_hook, code, wParam, lParam);
    }

    /// <summary>
    /// Every modifier physically down, BY SIDE, space separated -- empty when none are.
    /// </summary>
    /// <remarks>
    /// Sides, which <see cref="CurrentModifiers"/> deliberately does not distinguish: it asks
    /// VK_MENU and VK_CONTROL, the either-side keys, because a chord does not care which Alt. The
    /// open question does care, and is precisely "why this Alt and not that one". Win is included
    /// though no chord uses it -- it is what the desktop shortcuts inject, so a Win appearing here
    /// with nothing of ours running would name the culprit.
    /// </remarks>
    internal static string PhysicalModifierSides()
    {
        var held = new List<string>(4);
        foreach (var (key, name) in Sides)
        {
            if (IsPressed(key))
            {
                held.Add(name);
            }
        }

        return string.Join(" ", held);
    }

    private static readonly (int Key, string Name)[] Sides =
    [
        (0xA0, "LSHIFT"), (0xA1, "RSHIFT"),
        (0xA2, "LCTRL"), (0xA3, "RCTRL"),
        (0xA4, "LALT"), (0xA5, "RALT"),
        (0x5B, "LWIN"), (0x5C, "RWIN"),
    ];

    private static ModifierKeys CurrentModifiers()
    {
        var result = ModifierKeys.None;
        if (IsPressed(0x12)) result |= ModifierKeys.Alt;
        if (IsPressed(0x10)) result |= ModifierKeys.Shift;
        if (IsPressed(0x11)) result |= ModifierKeys.Control;
        return result;
    }

    private static bool IsPressed(int virtualKey) => (PInvoke.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
}
