using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.DirectComposition;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CosmicWin.Interop.Win32;

/// <summary>The pure style/placement decisions of <see cref="Win32MiniSceneWindow"/>, testable without a window.</summary>
internal static class MiniSceneWindowStyles
{
    /// <summary>
    /// Topmost (above ordinary windows), tool window (no taskbar/Alt+Tab), never activated,
    /// click-through, and no redirection bitmap so DirectComposition alone paints it (which is what
    /// lets a WebView2 composition visual be see-through).
    /// </summary>
    /// <remarks>
    /// <c>WS_EX_LAYERED</c> IS required: measured with WindowFromPoint, <c>WS_EX_TRANSPARENT</c> alone
    /// on this top-level window still returned OUR window at its center (not click-through); adding
    /// <c>WS_EX_LAYERED</c> makes hit-testing fall through to the window beneath. A layered window is
    /// only shown once layered attributes are set, so <see cref="Win32MiniSceneWindow"/> calls
    /// <c>SetLayeredWindowAttributes(alpha 255)</c> after creating it (a no-op for the pixels; the
    /// content still comes from DirectComposition).
    /// </remarks>
    public const WINDOW_EX_STYLE ExtendedStyle =
        WINDOW_EX_STYLE.WS_EX_TOPMOST
        | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW
        | WINDOW_EX_STYLE.WS_EX_NOACTIVATE
        | WINDOW_EX_STYLE.WS_EX_TRANSPARENT
        | WINDOW_EX_STYLE.WS_EX_LAYERED
        | WINDOW_EX_STYLE.WS_EX_NOREDIRECTIONBITMAP;

    /// <summary>Used by every positioning call: never steal focus, always visible.</summary>
    public const SET_WINDOW_POS_FLAGS PlacementFlags =
        SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW;

    /// <summary><c>HWND_TOPMOST</c> (-1): the insert-after handle that keeps the window in the topmost band.</summary>
    public static readonly nint InsertAfterTopmost = -1;
}

/// <summary>
/// A small top-level popup that shows one DirectComposition overlay visual and nothing else: the
/// host of the mini scene's WebView2. It has no swapchain, so its composition tree is just the root
/// visual and the overlay child a WebView2 composition controller renders into.
/// </summary>
/// <remarks>
/// Everything runs on the creating (UI) thread, so the COM wrappers are held directly instead of as
/// the cross-thread raw pointers <see cref="Win32VideoWallpaperHost"/> needs.
/// </remarks>
public sealed unsafe class Win32MiniSceneWindow : IMiniSceneSurface
{
    private readonly string _className = $"CosmicWinMiniScene-{Guid.NewGuid():N}";
    private WNDPROC? _wndProc;
    private SafeHandle? _hInstance;
    private bool _classRegistered;
    private bool _disposed;
    private HWND _hwnd;
    private IDCompositionDevice? _device;
    private IDCompositionTarget? _target;
    private IDCompositionVisual? _root;
    private IDCompositionVisual? _overlay;

    public nint Hwnd => (nint)_hwnd.Value;

    public bool IsCompositionReady => !_disposed && _root is not null;

    public int CompositionGeneration { get; private set; }

    public bool TryCreate(Rectangle bounds)
    {
        if (_disposed || !_hwnd.IsNull) return false;
        _wndProc = static (hwnd, msg, wParam, lParam) => PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
        _hInstance = PInvoke.GetModuleHandle((string?)null);
        var hInstance = new HINSTANCE(_hInstance.DangerousGetHandle());
        fixed (char* name = _className)
        {
            WNDCLASSEXW wc = new()
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = _wndProc,
                hInstance = hInstance,
                lpszClassName = name,
            };
            if (PInvoke.RegisterClassEx(wc) == 0) return false;
        }

        _classRegistered = true;
        _hwnd = PInvoke.CreateWindowEx(
            MiniSceneWindowStyles.ExtendedStyle, _className, "CosmicWin Mini Scene", WINDOW_STYLE.WS_POPUP,
            bounds.Left, bounds.Top, bounds.Width, bounds.Height, HWND.Null, null, _hInstance, null);
        if (_hwnd.IsNull) return false;
        PInvoke.SetLayeredWindowAttributes(_hwnd, default, 255, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);

        try
        {
            // A null DXGI device is the documented way to get a device that only composes visuals
            // (no surfaces), which is all a WebView2 visual target needs.
            PInvoke.DCompositionCreateDevice(null!, out _device);
            _device!.CreateTargetForHwnd(_hwnd, true, out _target);
            _device.CreateVisual(out _root);
            _target!.SetRoot(_root);
            _device.Commit();
            CompositionGeneration++;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool Place(Rectangle bounds) =>
        !_disposed && !_hwnd.IsNull && PInvoke.SetWindowPos(
            _hwnd, new HWND(MiniSceneWindowStyles.InsertAfterTopmost),
            bounds.Left, bounds.Top, bounds.Width, bounds.Height, MiniSceneWindowStyles.PlacementFlags);

    public object? AddCompositionOverlayVisual()
    {
        if (!IsCompositionReady || _device is null) return null;
        try
        {
            RemoveOverlayCore();
            _device.CreateVisual(out _overlay);
            _root!.AddVisual(_overlay, true, null);
            _device.Commit();
            return _overlay;
        }
        catch
        {
            return null;
        }
    }

    public void RemoveCompositionOverlayVisual()
    {
        try
        {
            RemoveOverlayCore();
            _device?.Commit();
        }
        catch
        {
            // A composition failure must never reach the caller.
        }
    }

    public void CommitComposition()
    {
        try { _device?.Commit(); }
        catch { /* see RemoveCompositionOverlayVisual */ }
    }

    private void RemoveOverlayCore()
    {
        if (_overlay is null) return;
        try { _root?.RemoveVisual(_overlay); }
        finally
        {
            (_overlay as IDisposable)?.Dispose();
            _overlay = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        RemoveCompositionOverlayVisual();
        _disposed = true;
        foreach (var com in new object?[] { _root, _target, _device })
        {
            (com as IDisposable)?.Dispose();
        }

        _root = null;
        _target = null;
        _device = null;
        if (!_hwnd.IsNull)
        {
            PInvoke.DestroyWindow(_hwnd);
            _hwnd = default;
        }

        if (_classRegistered)
        {
            PInvoke.UnregisterClass(_className, _hInstance!);
            _classRegistered = false;
        }
    }
}
