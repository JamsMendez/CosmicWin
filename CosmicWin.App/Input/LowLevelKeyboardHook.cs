using System.Threading.Channels;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CosmicWin.App.Input;

public sealed class KeyboardEventProcessor(ChordTable chords)
{
    private readonly bool[] _acceptedKeyDown = new bool[256];

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
        if (!chords.TryMatch(modifiers, key, out var action)) return false;
        return _acceptedKeyDown[keyIndex] = writer.TryWrite(action);
    }
}

internal delegate bool KeyboardHookCallback(KeyboardKey key, bool isKeyDown, ModifierKeys modifiers);

internal interface IKeyboardHookPlatform
{
    void Install(KeyboardHookCallback callback);
    bool Uninstall();
    void PumpMessages();
}

public sealed class LowLevelKeyboardHook : IDisposable
{
    public static readonly TimeSpan DefaultWatchdogInterval = TimeSpan.FromSeconds(5);
    private readonly ChannelWriter<HotkeyAction> _writer;
    private readonly IKeyboardHookPlatform _platform;
    private readonly TimeSpan _watchdogInterval;
    private readonly Func<long> _clock;
    private readonly CancellationTokenSource _stop = new();
    private readonly ManualResetEventSlim _started = new();
    private readonly KeyboardEventProcessor _processor = new(ChordTable.Default);
    private Thread? _thread;
    private Exception? _startupFailure;
    private long _lastActivity;

    public bool? UnhookSucceeded { get; private set; }

    public LowLevelKeyboardHook(ChannelWriter<HotkeyAction> writer)
        : this(writer, new WindowsKeyboardHookPlatform(), DefaultWatchdogInterval) { }

    internal LowLevelKeyboardHook(
        ChannelWriter<HotkeyAction> writer, IKeyboardHookPlatform platform,
        TimeSpan watchdogInterval, Func<long>? clock = null)
    {
        _writer = writer;
        _platform = platform;
        _watchdogInterval = watchdogInterval;
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
            _platform.Install(OnKeyboardEvent);
            _started.Set();
            while (!_stop.IsCancellationRequested)
            {
                _platform.PumpMessages();
                if (_clock() - Volatile.Read(ref _lastActivity) >= _watchdogInterval.TotalMilliseconds)
                {
                    RecordUninstall();
                    _platform.Install(OnKeyboardEvent);
                    Volatile.Write(ref _lastActivity, _clock());
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

    private bool OnKeyboardEvent(KeyboardKey key, bool isKeyDown, ModifierKeys modifiers)
    {
        Volatile.Write(ref _lastActivity, _clock());
        return _processor.Process(key, isKeyDown, modifiers, _writer);
    }

    private void RecordUninstall() =>
        UnhookSucceeded = (UnhookSucceeded ?? true) && _platform.Uninstall();

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
