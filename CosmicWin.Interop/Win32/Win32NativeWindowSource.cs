using System.Runtime.InteropServices;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CosmicWin.Interop.Win32;

/// <summary>
/// Real, CsWin32-backed <see cref="INativeWindowSource"/>: enumerates via <c>EnumWindows</c>,
/// reads geometry/title via <c>GetWindowRect</c>/<c>GetWindowTextW</c>, and subscribes to
/// create/destroy/move-resize notifications via <c>SetWinEventHook</c>. Ports the pattern from
/// fancywm's <c>WinMan.Windows.WinEventHookHelper</c> (algorithm only — original code).
/// </summary>
internal sealed unsafe class Win32NativeWindowSource : INativeWindowSource
{
    public IReadOnlyList<nint> EnumerateTopLevelWindows()
    {
        var handles = new List<nint>();

        PInvoke.EnumWindows(
            (hwnd, _) =>
            {
                if (IsTrackable(hwnd))
                {
                    handles.Add(hwnd);
                }

                return true;
            },
            default(LPARAM));

        return handles;
    }

    public bool TryGetWindowInfo(nint hwnd, out NativeWindowInfo info)
    {
        HWND handle = new(hwnd);

        if (!PInvoke.IsWindow(handle))
        {
            info = default;
            return false;
        }

        if (!PInvoke.GetWindowRect(handle, out RECT rect))
        {
            info = default;
            return false;
        }

        info = new NativeWindowInfo(
            ReadWindowTitle(handle),
            new Rectangle(rect.left, rect.top, rect.right, rect.bottom),
            ReadClassName(handle),
            ReadProcessName(handle),
            ReadStyle(handle),
            ReadExStyle(handle),
            ReadIsOwned(handle));
        return true;
    }

    public bool SetWindowPosition(nint hwnd, Rectangle bounds)
    {
        HWND handle = new(hwnd);

        return PInvoke.SetWindowPos(
            handle,
            HWND.Null,
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
    }

    public IDisposable SubscribeWindowEvents(NativeWindowEventCallback callback)
    {
        return new WinEventHookSubscription(callback);
    }

    /// <summary>How long <see cref="Activate"/> waits for its input-attached worker before giving up.</summary>
    private static readonly TimeSpan ActivationTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// MR-2 (Engram discovery #106). The fourth supervised run recorded 40 focus chords and every
    /// activation failed: once the App layer stopped trusting its own optimistic focus cache, the
    /// earlier bare-<c>AttachThreadInput</c> fix was revealed to have never worked at all. It ran on
    /// <c>ActionDispatcher.RunAsync</c>'s thread-pool thread, and <c>AttachThreadInput</c> shares
    /// INPUT queues -- a thread-pool thread has none, so there was nothing to attach to.
    /// <para>
    /// This is the escalation the vendored FancyWM reference (<c>FancyWM/Utilities/FocusHelper.cs</c>)
    /// uses, algorithm only: try plainly, then from a dedicated thread that first CREATES a message
    /// queue and attaches to the foreground thread's input, then -- only if that is still refused --
    /// release Windows' foreground lock with two synthetic Alt taps and retry. The synthetic input
    /// is deliberately the LAST rung: it is real <c>VK_MENU</c> traffic on the user's desktop and can
    /// trip menu accelerators, so it is never paid for until the cheaper rungs have failed.
    /// </para>
    /// <para>
    /// Every rung VERIFIES with <c>GetForegroundWindow</c> instead of trusting
    /// <c>SetForegroundWindow</c>'s return value, because that value was measured claiming success
    /// while nothing moved on screen.
    /// </para>
    /// </summary>
    public ActivationOutcome Activate(nint hwnd)
    {
        HWND target = new(hwnd);
        if (PInvoke.GetForegroundWindow() == target)
        {
            return ActivationOutcome.AlreadyForeground;
        }

        if (TrySetForeground(target))
        {
            return ActivationOutcome.Direct;
        }

        var outcome = ActivationOutcome.Failed;
        var worker = new Thread(() => outcome = ActivateFromAttachedInput(target)) { IsBackground = true };
        worker.Start();
        return worker.Join(ActivationTimeout) ? outcome : ActivationOutcome.Failed;
    }

    /// <summary>
    /// <see cref="INativeWindowSource"/>'s boolean contract, unchanged for callers, now backed by
    /// <see cref="Activate"/>. True means the OS itself confirmed the target holds the foreground.
    /// </summary>
    public bool TryActivateWindow(nint hwnd) => Activate(hwnd) != ActivationOutcome.Failed;

    /// <summary>Asks for the foreground and then CHECKS, rather than believing the return value.</summary>
    private static bool TrySetForeground(HWND target)
    {
        PInvoke.SetForegroundWindow(target);
        return PInvoke.GetForegroundWindow() == target;
    }

    /// <summary>
    /// Runs on its own thread so it can own a message queue: <c>PeekMessage</c> creates one, which is
    /// the precondition <c>AttachThreadInput</c> needs and the piece the previous fix was missing.
    /// </summary>
    private static ActivationOutcome ActivateFromAttachedInput(HWND target)
    {
        PInvoke.PeekMessage(out _, new HWND(-1), 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_NOREMOVE);

        HWND foreground = PInvoke.GetForegroundWindow();
        uint foregroundThreadId = foreground == HWND.Null ? 0 : PInvoke.GetWindowThreadProcessId(foreground, null);
        uint currentThreadId = PInvoke.GetCurrentThreadId();
        bool attached = foregroundThreadId != 0 && foregroundThreadId != currentThreadId
            && PInvoke.AttachThreadInput(currentThreadId, foregroundThreadId, true);

        try
        {
            if (TrySetForeground(target))
            {
                return ActivationOutcome.AttachedInput;
            }

            SendAltTaps();
            return TrySetForeground(target) ? ActivationOutcome.InputUnlocked : ActivationOutcome.Failed;
        }
        finally
        {
            if (attached)
            {
                PInvoke.AttachThreadInput(currentThreadId, foregroundThreadId, false);
            }
        }
    }

    /// <summary>
    /// Two full Alt press/release pairs. Windows grants the foreground to whoever received the last
    /// input event, so this makes THIS process that whoever. Alt specifically because a tiling chord
    /// already holds it down, which makes these the least surprising synthetic keys available here.
    /// </summary>
    private static void SendAltTaps()
    {
        var inputs = new INPUT[4];
        for (int i = 0; i < inputs.Length; i++)
        {
            inputs[i].type = INPUT_TYPE.INPUT_KEYBOARD;
            inputs[i].Anonymous.ki.wVk = VIRTUAL_KEY.VK_MENU;
            inputs[i].Anonymous.ki.dwFlags = (i % 2) == 0
                ? KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY
                : KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY | KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP;
        }

        PInvoke.SendInput(inputs, Marshal.SizeOf<INPUT>());
    }

    private static bool IsTrackable(HWND hwnd)
    {
        if (!PInvoke.IsWindowVisible(hwnd))
        {
            return false;
        }

        // Owned windows (e.g. tool tips, dropdowns) are not independently trackable top-level
        // application windows. Fine-grained exclusion heuristics (WE-1: TOOLWINDOW, dialogs,
        // no-sysmenu) belong to CosmicWin.Layout's WindowDescriptor/Filters (Phase 3) — Interop
        // only decides what is trackable at all, not what the tiling engine should tile.
        var hasOwner = PInvoke.GetWindow(hwnd, GET_WINDOW_CMD.GW_OWNER) != HWND.Null;
        return IsTrackable(hasOwner, ReadIsCloaked(hwnd));
    }

    /// <summary>
    /// MR-1: pure decision extracted from the two raw Win32 reads in <see cref="IsTrackable(HWND)"/>
    /// so the phantom-window exclusion is unit-testable without a live cloaked HWND -- DWM
    /// cloaking cannot be self-triggered by a spawned test window (it is driven entirely by
    /// OS-internal virtual-desktop switching / UWP suspension). See
    /// <c>Win32NativeWindowSourceCloakingTests</c>, which pins this against the exact descriptor
    /// shape (unowned, cloaked) measured on a real desktop enumeration.
    /// </summary>
    internal static bool IsTrackable(bool hasOwner, bool isCloaked) => !hasOwner && !isCloaked;

    /// <summary>
    /// MR-1 (2026-08-22): a real desktop enumeration found DWM-cloaked windows -- e.g. a
    /// suspended UWP host frame (<c>ApplicationFrameWindow</c>, owned by <c>explorer.exe</c>
    /// itself) -- passing every other WE-1/Interop check while <c>IsWindowVisible</c> still
    /// reports <c>true</c> and nothing renders on screen. Such a window silently occupied a
    /// tiling slot, so real windows never got their full share of the work area. A failed
    /// <c>DwmGetWindowAttribute</c> call (non-success HRESULT) degrades to "not cloaked" --
    /// fail-open, matching every other raw read in this class, since a diagnostic-read failure
    /// must never exclude a window that IS actually visible.
    /// </summary>
    private static bool ReadIsCloaked(HWND hwnd)
    {
        int cloaked;
        var hr = PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, (uint)sizeof(int));
        return hr.Succeeded && cloaked != 0;
    }

    private static string ReadWindowTitle(HWND hwnd)
    {
        int length = PInvoke.GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        Span<char> buffer = length < 256 ? stackalloc char[length + 1] : new char[length + 1];
        int written;
        fixed (char* pBuffer = buffer)
        {
            written = PInvoke.GetWindowText(hwnd, pBuffer, buffer.Length);
        }

        return written > 0 ? new string(buffer[..written]) : string.Empty;
    }

    /// <summary>Raw GWL_STYLE/GWL_EXSTYLE reads (WindowFilters interprets the bits). 32-bit GetWindowLong, not the Ptr variant — not generatable for AnyCPU (PInvoke005); both values are always 32-bit anyway.</summary>
    private static uint ReadStyle(HWND hwnd) => unchecked((uint)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE));

    private static uint ReadExStyle(HWND hwnd) => unchecked((uint)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE));

    /// <summary>The raw ownership signal WE-1's owned-dialog clause needs.</summary>
    private static bool ReadIsOwned(HWND hwnd) => PInvoke.GetWindow(hwnd, GET_WINDOW_CMD.GW_OWNER) != HWND.Null;

    private static string ReadClassName(HWND hwnd)
    {
        Span<char> buffer = stackalloc char[256];
        int written;
        fixed (char* pBuffer = buffer)
        {
            written = PInvoke.GetClassName(hwnd, pBuffer, buffer.Length);
        }

        return written > 0 ? new string(buffer[..written]) : string.Empty;
    }

    /// <summary>Owning process's executable file name incl. <c>.exe</c> (NOT <c>Process.ProcessName</c>, which strips it). Degrades to empty rather than throwing on any failure.</summary>
    private static string ReadProcessName(HWND hwnd)
    {
        try
        {
            uint processId = 0;
            PInvoke.GetWindowThreadProcessId(hwnd, &processId);
            if (processId == 0)
            {
                return string.Empty;
            }

            using var process = PInvoke.OpenProcess_SafeHandle(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (process.IsInvalid)
            {
                return string.Empty;
            }

            Span<char> buffer = stackalloc char[260];
            uint size = (uint)buffer.Length;
            bool ok = PInvoke.QueryFullProcessImageName(process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer, ref size);

            if (!ok || size == 0)
            {
                return string.Empty;
            }

            return Path.GetFileName(new string(buffer[..(int)size]));
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Owns the lifetime of one <c>SetWinEventHook</c> registration. The delegate passed to
    /// <c>SetWinEventHook</c> must be kept alive for as long as the hook is installed, or the
    /// GC may collect it while native code still holds the function pointer.
    /// </summary>
    private sealed class WinEventHookSubscription : IDisposable
    {
        private readonly WINEVENTPROC _thunk;
        private readonly HWINEVENTHOOK _hook;

        /// <summary>
        /// A SECOND registration is required, not a widened range: the object events above live at
        /// 0x8000+ while MOVESIZESTART/END are 0x000A/0x000B, and one hook spanning both would ask
        /// the OS to deliver every system event in between.
        /// </summary>
        private readonly HWINEVENTHOOK _moveSizeHook;
        private NativeWindowEventCallback? _callback;

        public WinEventHookSubscription(NativeWindowEventCallback callback)
        {
            _callback = callback;
            _thunk = OnWinEvent;

            _hook = PInvoke.SetWinEventHook(
                PInvoke.EVENT_OBJECT_CREATE,
                PInvoke.EVENT_OBJECT_LOCATIONCHANGE,
                HMODULE.Null,
                _thunk,
                idProcess: 0,
                idThread: 0,
                PInvoke.WINEVENT_OUTOFCONTEXT | PInvoke.WINEVENT_SKIPOWNPROCESS);

            if (_hook.IsNull)
            {
                throw new InvalidOperationException("SetWinEventHook failed.");
            }

            _moveSizeHook = PInvoke.SetWinEventHook(
                PInvoke.EVENT_SYSTEM_MOVESIZESTART,
                PInvoke.EVENT_SYSTEM_MOVESIZEEND,
                HMODULE.Null,
                _thunk,
                idProcess: 0,
                idThread: 0,
                PInvoke.WINEVENT_OUTOFCONTEXT | PInvoke.WINEVENT_SKIPOWNPROCESS);

            if (_moveSizeHook.IsNull)
            {
                PInvoke.UnhookWinEvent(_hook);
                throw new InvalidOperationException("SetWinEventHook failed for the move/size range.");
            }
        }

        private void OnWinEvent(HWINEVENTHOOK hWinEventHook, uint eventType, HWND hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
        {
            if (idObject != (int)OBJECT_IDENTIFIER.OBJID_WINDOW || idChild != 0 || hwnd == HWND.Null)
            {
                return;
            }

            var callback = _callback;
            if (callback is null)
            {
                return;
            }

            switch (eventType)
            {
                // EVENT_OBJECT_SHOW was already arriving inside the subscribed range with no case
                // for it (Engram discovery #106). Trackability requires IsWindowVisible, and a
                // top-level window is normally created HIDDEN and shown a moment later, so the
                // CREATE arm dropped such a window and nothing looked again -- Win32Workspace.Poll()
                // runs on no production path. Win32Workspace.TryAddWindow is idempotent, so the two
                // arms reporting the same window costs nothing.
                case PInvoke.EVENT_OBJECT_CREATE:
                case PInvoke.EVENT_OBJECT_SHOW:
                    if (IsTrackable(hwnd))
                    {
                        callback(NativeWindowEventKind.Created, hwnd);
                    }

                    break;
                case PInvoke.EVENT_OBJECT_DESTROY:
                    callback(NativeWindowEventKind.Destroyed, hwnd);
                    break;
                case PInvoke.EVENT_OBJECT_LOCATIONCHANGE:
                    if (IsTrackable(hwnd))
                    {
                        callback(NativeWindowEventKind.BoundsChanged, hwnd);
                    }

                    break;

                // Brackets one hand-driven move/resize. Deliberately NOT gated on IsTrackable: the
                // bracket must close for whatever it opened on, or a window that stops being
                // trackable mid-gesture would leave the drag flag set forever.
                case PInvoke.EVENT_SYSTEM_MOVESIZESTART:
                    callback(NativeWindowEventKind.MoveSizeStarted, hwnd);
                    break;
                case PInvoke.EVENT_SYSTEM_MOVESIZEEND:
                    callback(NativeWindowEventKind.MoveSizeEnded, hwnd);
                    break;
            }
        }

        public void Dispose()
        {
            _callback = null;
            if (!_hook.IsNull)
            {
                PInvoke.UnhookWinEvent(_hook);
            }

            if (!_moveSizeHook.IsNull)
            {
                PInvoke.UnhookWinEvent(_moveSizeHook);
            }
        }
    }
}
