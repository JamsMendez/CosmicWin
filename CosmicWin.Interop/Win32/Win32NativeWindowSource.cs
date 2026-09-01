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
/// create/destroy/move-resize notifications via <c>SetWinEventHook</c>.
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

        // The DRAWN frame, not GetWindowRect's, so every layer above reasons about what the user
        // can actually see. DWM declining (an old-style window with no extended frame) falls back
        // to the raw rectangle, where the two are the same thing anyway.
        var windowRect = new Rectangle(rect.left, rect.top, rect.right, rect.bottom);
        if (!TryGetDrawnFrameBounds(hwnd, out var drawn))
        {
            drawn = windowRect;
        }

        info = new NativeWindowInfo(
            ReadWindowTitle(handle),
            drawn,
            ReadClassName(handle),
            ReadProcessName(handle),
            ReadStyle(handle),
            ReadExStyle(handle),
            ReadIsOwned(handle),
            PInvoke.IsWindowVisible(handle));
        return true;
    }

    /// <summary>
    /// Places the window so its DRAWN frame lands on <paramref name="bounds"/>. SetWindowPos speaks
    /// GetWindowRect coordinates, which include the invisible resize border -- measured on this
    /// build as 7px left/right/bottom and 0 top -- so asking for a tile verbatim leaves the visible
    /// window inset on three sides and flush on the fourth. The current inset is read back per call
    /// rather than assumed: it varies by window style, DPI and Windows version.
    /// </summary>
    public bool SetWindowPosition(nint hwnd, Rectangle bounds)
    {
        HWND handle = new(hwnd);
        var target = bounds;

        if (PInvoke.GetWindowRect(handle, out RECT current) &&
            TryGetDrawnFrameBounds(hwnd, out var drawn) &&
            drawn.Width > 0 && drawn.Height > 0)
        {
            target = new Rectangle(
                bounds.Left - (drawn.Left - current.left),
                bounds.Top - (drawn.Top - current.top),
                bounds.Right + (current.right - drawn.Right),
                bounds.Bottom + (current.bottom - drawn.Bottom));
        }

        return PInvoke.SetWindowPos(
            handle,
            HWND.Null,
            target.Left,
            target.Top,
            target.Width,
            target.Height,
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
    }

    public IDisposable SubscribeWindowEvents(NativeWindowEventCallback callback)
    {
        return new WinEventHookSubscription(callback);
    }

    public IDisposable SubscribeShownWindows(Action<nint> callback)
    {
        return new ShownWindowHookSubscription(callback);
    }

    /// <summary>How long <see cref="Activate"/> waits for its input-attached worker before giving up.</summary>
    private static readonly TimeSpan ActivationTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// MR-2. The fourth supervised run recorded 40 focus chords and every
    /// activation failed: once the App layer stopped trusting its own optimistic focus cache, the
    /// earlier bare-<c>AttachThreadInput</c> fix was revealed to have never worked at all. It ran on
    /// <c>ActionDispatcher.RunAsync</c>'s thread-pool thread, and <c>AttachThreadInput</c> shares
    /// INPUT queues -- a thread-pool thread has none, so there was nothing to attach to.
    /// <para>
    /// So activation escalates through rungs: try plainly, then from a dedicated thread that first CREATES a message
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

        return RunBounded(() => ActivateFromAttachedInput(target), ActivationTimeout);
    }

    /// <summary>
    /// Posts <c>WM_CLOSE</c>. The application decides what happens next, including nothing.
    /// </summary>
    /// <remarks>
    /// Deliberately the polite ask and nothing stronger. There is no fallback to
    /// <c>DestroyWindow</c> (which cannot cross a process boundary anyway) and none to
    /// <c>TerminateProcess</c>: a window manager that can discard a user's unsaved work because an
    /// application took too long to answer a keystroke has picked the wrong default.
    /// <para>
    /// <c>PostMessage</c>, never <c>SendMessage</c>. Sending blocks until the target has finished
    /// handling it, and "finished" can mean a modal save prompt waiting on a human. Today's own
    /// lesson, one layer down: an unbounded wait on another process, taken on the thread that also
    /// draws the focus border.
    /// </para>
    /// </remarks>
    public bool TryClose(nint hwnd)
    {
        HWND target = new(hwnd);
        return PInvoke.IsWindow(target) && PInvoke.PostMessage(target, PInvoke.WM_CLOSE, default, default);
    }

    /// <summary>
    /// Runs <paramref name="attempt"/> on a dedicated thread and waits at most
    /// <paramref name="budget"/> for it, reporting the two endings SEPARATELY.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The attempt needs its own thread because <c>AttachThreadInput</c> shares INPUT queues and a
    /// thread-pool thread has none -- MR-2's fourth supervised run recorded 40 chords and 40 failed
    /// activations before that was understood.
    /// </para>
    /// <para>
    /// The budget bounds a thread that must first be SCHEDULED, so exhausting it says nothing about
    /// what the OS would have answered. Returning <see cref="ActivationOutcome.Failed"/> there --
    /// documented as "every rung was refused" -- asserted knowledge this path does not have, and
    /// made every red activation unattributable. It reports <see cref="ActivationOutcome.TimedOut"/>
    /// now, so the next red run names its own cause instead of leaving it to be guessed at.
    /// </para>
    /// <para>
    /// Takes the attempt as a delegate so both endings can be forced headlessly, without a desktop
    /// or a real window. A bound that only the weather can exercise is a bound nobody has tested.
    /// </para>
    /// </remarks>
    internal static ActivationOutcome RunBounded(Func<ActivationOutcome> attempt, TimeSpan budget)
    {
        var outcome = ActivationOutcome.Failed;
        var worker = new Thread(() => outcome = attempt()) { IsBackground = true };
        worker.Start();
        return worker.Join(budget) ? outcome : ActivationOutcome.TimedOut;
    }

    /// <summary>
    /// Whether an outcome means the OS CONFIRMED the target holds the foreground. Both failing
    /// endings answer false: a timeout confirms nothing, so the split serves the diagnosis and never
    /// changes what a caller sees.
    /// </summary>
    internal static bool Activated(ActivationOutcome outcome) => outcome.Confirmed();

    /// <summary>
    /// The boolean reading, unchanged for callers that only need "did focus move", now backed by
    /// <see cref="Activate"/>. True means the OS itself confirmed the target holds the foreground.
    /// </summary>
    /// <remarks>
    /// No longer part of <see cref="INativeWindowSource"/>: the interface carries the outcome, and
    /// a boolean member beside it would just reintroduce the flattening one layer down.
    /// </remarks>
    public bool TryActivateWindow(nint hwnd) => Activate(hwnd).Confirmed();

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

    /// <summary>
    /// <c>WS_CHILD</c>. Named here rather than pulled from the generated enum because this file
    /// already reads raw style bits through <see cref="ReadStyle"/> and interprets none of the
    /// others -- <c>WindowStyleFlags</c> in the layout side owns that job, and it is Win32-free.
    /// </summary>
    private const uint WsChild = 0x40000000;

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

        // A CHILD is not a window this manager has any business tiling, and asking is what makes
        // the two admission paths agree. Adoption enumerates through EnumWindows, which returns
        // only top-level windows by construction; the event path takes a raw HWND from a
        // desktop-wide hook, which reports children too. Measured: restarting with Clipchamp open
        // laid it out correctly at its full half, while opening it under a running CosmicWin let
        // its own title bar and caption buttons into the tree and squeezed it into a quarter.
        //
        // WS_CHILD rather than a class name, deliberately -- a list of offenders is always one
        // release behind, and this is the same question EnumWindows already answers.
        var isChild = (ReadStyle(hwnd) & WsChild) != 0;
        return IsTrackable(hasOwner, ReadIsCloaked(hwnd), isChild);
    }

    /// <summary>
    /// MR-1: pure decision extracted from the two raw Win32 reads in <see cref="IsTrackable(HWND)"/>
    /// so the phantom-window exclusion is unit-testable without a live cloaked HWND. See
    /// <c>Win32NativeWindowSourceCloakingTests</c>, which pins this against the exact descriptor
    /// shape (unowned, cloaked) measured on a real desktop enumeration.
    /// <para>
    /// This used to add that DWM cloaking cannot be self-triggered by a spawned test window, driven
    /// entirely by OS-internal desktop switching and UWP suspension. That was true when it was
    /// written and is not any more: <c>Win32VirtualDesktopService.TrySwitchTo</c> switches
    /// desktops on purpose, and <c>DesktopSwitchVisibilityTests</c> uses it to cloak a real window
    /// and wait for the cloak to take effect. The pure predicate remains the cheapest level that
    /// proves this decision, which is reason enough without an impossibility that expired.
    /// </para>
    /// </summary>
    internal static bool IsTrackable(bool hasOwner, bool isCloaked, bool isChild) =>
        !hasOwner && !isCloaked && !isChild;

    /// <summary>
    /// The exact complement of <see cref="IsTrackable(bool, bool)"/>'s owner half, extracted for the
    /// same reason: the raw <c>GetWindow</c> read cannot be unit-tested without a live HWND, so the
    /// decision is separated from it.
    /// </summary>
    /// <remarks>
    /// The two hooks partition the desktop rather than overlap it. The tiling hook takes
    /// <c>!hasOwner</c>; this one takes the rest, because a window WITH an owner is precisely what
    /// that gate drops and precisely where a modal dialog lives. Nothing unowned is ever reported
    /// here -- the tiling path already has it, and reporting it twice would invite two answers about
    /// where the same window belongs.
    /// </remarks>
    internal static bool IsShownWindowWorthReporting(bool hasOwner) => hasOwner;

    /// <summary>
    /// MR-1 : a real desktop enumeration found DWM-cloaked windows -- e.g. a
    /// suspended UWP host frame (<c>ApplicationFrameWindow</c>, owned by <c>explorer.exe</c>
    /// itself) -- passing every other WE-1/Interop check while <c>IsWindowVisible</c> still
    /// reports <c>true</c> and nothing renders on screen. Such a window silently occupied a
    /// tiling slot, so real windows never got their full share of the work area. A failed
    /// <c>DwmGetWindowAttribute</c> call (non-success HRESULT) degrades to "not cloaked" --
    /// fail-open, matching every other raw read in this class, since a diagnostic-read failure
    /// must never exclude a window that IS actually visible.
    /// </summary>
    /// <summary>
    /// What DWM actually PAINTS for this window, as opposed to what <c>GetWindowRect</c> reports.
    /// The two differ by the invisible resize border, which on modern Windows is present on the
    /// left, right and bottom and absent on the top -- so tiling on GetWindowRect coordinates alone
    /// leaves visible gaps on three sides and none on the fourth. Returns <see langword="false"/>
    /// (rather than throwing) when DWM declines, which callers must treat as "no inset known".
    /// </summary>
    internal static unsafe bool TryGetDrawnFrameBounds(nint hwnd, out Rectangle bounds)
    {
        RECT rect;
        var hr = PInvoke.DwmGetWindowAttribute(
            new HWND(hwnd),
            DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS,
            &rect,
            (uint)sizeof(RECT));

        if (hr.Failed)
        {
            bounds = Rectangle.Empty;
            return false;
        }

        bounds = new Rectangle(rect.left, rect.top, rect.right, rect.bottom);
        return true;
    }

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

        /// <summary>
        /// A THIRD registration, for <c>EVENT_OBJECT_UNCLOAKED</c> (0x8018) alone.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Measured with a real Settings launch: a UWP window is born CLOAKED and stays cloaked
        /// through its own CREATE (+0ms) and SHOW (+125ms), so <see cref="IsTrackable(HWND)"/>
        /// refuses it at both. It uncloaks at +190ms, and that event lands past the end of the range
        /// above -- so the ONE event announcing that the window became trackable was the one nobody
        /// listened to. What admitted it instead was whatever unrelated event happened to be
        /// delivered after the uncloak, and failing that the two-second reconciliation tick: a
        /// window joined the layout by luck, roughly a second late.
        /// </para>
        /// <para>
        /// The CLOAK half is deliberately absent, and must stay absent. DWM cloaks every window on a
        /// virtual desktop the user leaves, so treating it as a removal is precisely the regression
        /// that once dismantled the whole layout on a desktop switch.
        /// </para>
        /// <para>
        /// One event, not a range widened to reach it: 0x800C..0x8017 sits in between and would be
        /// delivered for every window on the desktop for nothing.
        /// </para>
        /// </remarks>
        private readonly HWINEVENTHOOK _uncloakHook;
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

            _uncloakHook = PInvoke.SetWinEventHook(
                PInvoke.EVENT_OBJECT_UNCLOAKED,
                PInvoke.EVENT_OBJECT_UNCLOAKED,
                HMODULE.Null,
                _thunk,
                idProcess: 0,
                idThread: 0,
                PInvoke.WINEVENT_OUTOFCONTEXT | PInvoke.WINEVENT_SKIPOWNPROCESS);

            if (_uncloakHook.IsNull)
            {
                PInvoke.UnhookWinEvent(_hook);
                PInvoke.UnhookWinEvent(_moveSizeHook);
                throw new InvalidOperationException("SetWinEventHook failed for the uncloak event.");
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
                // for it. Trackability requires IsWindowVisible, and a
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

                // The exact counterpart of the SHOW arm above, and it was missing for the same
                // reason: HIDE sits inside the subscribed range (0x8003) and simply had no case, so
                // every one of them was dropped. Reported with Discord -- an application that lives
                // in the notification area is CLOSED by hiding its window, never by destroying it,
                // so the destroy arm never fired and the window kept its tile and its focus border
                // for the rest of the session.
                //
                // Re-checked rather than trusted: this is an out-of-context hook, so the callback
                // runs some time after the event, and a window that has already been shown again by
                // then is not hidden -- reporting it would evict a window the user is looking at.
                case PInvoke.EVENT_OBJECT_HIDE:
                    if (!PInvoke.IsWindowVisible(hwnd))
                    {
                        callback(NativeWindowEventKind.Hidden, hwnd);
                    }

                    break;
                case PInvoke.EVENT_OBJECT_LOCATIONCHANGE:
                    if (IsTrackable(hwnd))
                    {
                        callback(NativeWindowEventKind.BoundsChanged, hwnd);
                    }

                    break;

                // Its OWN kind, and it used to be CREATED. The old reasoning -- a window that has
                // just become trackable is, to everything downstream, a window that has just
                // arrived -- held exactly as long as nothing downstream cared which it was.
                // Something does now: the arriving-window redirect overrules where Windows chose to
                // put a NEW window, and an uncloak reported as a birth handed it every window on
                // every desktop the user walked back to. Measured: two populated desktops, and the
                // second emptied into the first.
                //
                // Still gated on IsTrackable, and TryAddWindow is still idempotent, so returning to
                // a desktop costs one dictionary lookup per window and announces nothing.
                case PInvoke.EVENT_OBJECT_UNCLOAKED:
                    if (IsTrackable(hwnd))
                    {
                        callback(NativeWindowEventKind.Uncloaked, hwnd);
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

            if (!_uncloakHook.IsNull)
            {
                PInvoke.UnhookWinEvent(_uncloakHook);
            }
        }
    }

    /// <summary>
    /// A SECOND, deliberately narrow <c>SetWinEventHook</c> registration: <c>EVENT_OBJECT_SHOW</c>
    /// alone, with no trackability gate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It exists so modal dialogs can be seen at all. <see cref="WinEventHookSubscription"/> gates
    /// its create/show arm on <see cref="IsTrackable(HWND)"/> -- <c>!hasOwner &amp;&amp;
    /// !isCloaked</c> -- and every modal has an owner, so no dialog has ever reached a caller
    /// through that path. Relaxing the gate there was the alternative and is the worse trade: it is
    /// the one function keeping tooltips, dropdowns, context menus and IME candidate lists out of
    /// the tiling pipeline, and every one of them would have arrived alongside the dialogs.
    /// </para>
    /// <para>
    /// One event, not a range. The tiling hook spans <c>EVENT_OBJECT_CREATE</c>..
    /// <c>EVENT_OBJECT_LOCATIONCHANGE</c> because it needs all of them; asking the OS for a range
    /// here would deliver every event in between for no reason, on every window on the desktop.
    /// </para>
    /// </remarks>
    private sealed class ShownWindowHookSubscription : IDisposable
    {
        private readonly WINEVENTPROC _thunk;
        private readonly HWINEVENTHOOK _hook;
        private Action<nint>? _callback;

        public ShownWindowHookSubscription(Action<nint> callback)
        {
            _callback = callback;
            _thunk = OnWinEvent;

            _hook = PInvoke.SetWinEventHook(
                PInvoke.EVENT_OBJECT_SHOW,
                PInvoke.EVENT_OBJECT_SHOW,
                HMODULE.Null,
                _thunk,
                idProcess: 0,
                idThread: 0,
                PInvoke.WINEVENT_OUTOFCONTEXT | PInvoke.WINEVENT_SKIPOWNPROCESS);

            if (_hook.IsNull)
            {
                throw new InvalidOperationException("SetWinEventHook failed for the shown-window event.");
            }
        }

        private void OnWinEvent(HWINEVENTHOOK hWinEventHook, uint eventType, HWND hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
        {
            // The window itself, not one of its controls. A dialog shows its buttons and labels on
            // the way up, and each of those arrives here as a child object.
            if (idObject != (int)OBJECT_IDENTIFIER.OBJID_WINDOW || idChild != 0 || hwnd == HWND.Null)
            {
                return;
            }

            // One GetWindow call, and it pays for itself. Everything past this point reads the
            // window in full -- title, class, rectangle, DWM frame, and a process handle -- and this
            // hook fires for every window shown anywhere on the desktop. Paying that for windows the
            // caller cannot possibly want would put an OpenProcess behind every menu that opens.
            if (!IsShownWindowWorthReporting(PInvoke.GetWindow(hwnd, GET_WINDOW_CMD.GW_OWNER) != HWND.Null))
            {
                return;
            }

            _callback?.Invoke(hwnd);
        }

        public void Dispose()
        {
            _callback = null;
            if (!_hook.IsNull)
            {
                PInvoke.UnhookWinEvent(_hook);
            }
        }
    }
}
