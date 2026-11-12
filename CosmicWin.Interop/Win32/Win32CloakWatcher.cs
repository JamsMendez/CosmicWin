using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CosmicWin.Interop.Win32;

/// <summary>
/// Reports every top-level window the moment DWM cloaks it -- which is what the shell does to every
/// window on a virtual desktop the user is leaving.
/// </summary>
/// <remarks>
/// <para>
/// It exists for the focus border. The overlay is a tool window, and the shell does not cloak tool
/// windows on a desktop switch, so the border stayed on screen after the window it framed had gone:
/// reported as the departing desktop's border flashing on the arriving one, for as long as it took
/// the next watch tick to notice. Hiding it on the cloak itself takes the tick out of that path.
/// </para>
/// <para>
/// Deliberately its own registration, apart from the workspace's hooks. Those must NOT see a cloak:
/// treating it as a removal is the regression that once dismantled the layout on a desktop switch.
/// One event, not a range, so nothing else on the desktop is delivered for it.
/// </para>
/// <para>
/// Out of context, so the callback runs on the thread that called <see cref="Watch"/>, and only
/// while that thread pumps messages -- the UI thread in production.
/// </para>
/// </remarks>
public sealed class Win32CloakWatcher : IDisposable
{
    private readonly WINEVENTPROC _thunk;
    private readonly HWINEVENTHOOK _hook;
    private Action<nint>? _cloaked;

    private Win32CloakWatcher(Action<nint> cloaked)
    {
        _cloaked = cloaked;
        _thunk = OnWinEvent;
        _hook = PInvoke.SetWinEventHook(
            PInvoke.EVENT_OBJECT_CLOAKED,
            PInvoke.EVENT_OBJECT_CLOAKED,
            HMODULE.Null,
            _thunk,
            idProcess: 0,
            idThread: 0,
            PInvoke.WINEVENT_OUTOFCONTEXT);
    }

    /// <summary>
    /// Starts reporting cloaked windows to <paramref name="cloaked"/>, or returns
    /// <see langword="null"/> when the hook cannot be installed -- the border then falls back to the
    /// watch tick, which is late but never wrong.
    /// </summary>
    public static Win32CloakWatcher? Watch(Action<nint> cloaked)
    {
        var watcher = new Win32CloakWatcher(cloaked);
        if (watcher._hook.IsNull)
        {
            return null;
        }

        return watcher;
    }

    private void OnWinEvent(
        HWINEVENTHOOK hWinEventHook, uint eventType, HWND hwnd, int idObject, int idChild,
        uint idEventThread, uint dwmsEventTime)
    {
        if (idObject != (int)OBJECT_IDENTIFIER.OBJID_WINDOW || idChild != 0 || hwnd == HWND.Null)
        {
            return;
        }

        _cloaked?.Invoke(hwnd);
    }

    public void Dispose()
    {
        if (_cloaked is null)
        {
            return;
        }

        _cloaked = null;
        PInvoke.UnhookWinEvent(_hook);
    }
}
