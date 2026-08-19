using System.Runtime.InteropServices;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
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

        info = new NativeWindowInfo(ReadWindowTitle(handle), new Rectangle(rect.left, rect.top, rect.right, rect.bottom));
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
        return PInvoke.GetWindow(hwnd, GET_WINDOW_CMD.GW_OWNER) == HWND.Null;
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

    /// <summary>
    /// Owns the lifetime of one <c>SetWinEventHook</c> registration. The delegate passed to
    /// <c>SetWinEventHook</c> must be kept alive for as long as the hook is installed, or the
    /// GC may collect it while native code still holds the function pointer.
    /// </summary>
    private sealed class WinEventHookSubscription : IDisposable
    {
        private readonly WINEVENTPROC _thunk;
        private readonly HWINEVENTHOOK _hook;
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
                case PInvoke.EVENT_OBJECT_CREATE:
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
            }
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
