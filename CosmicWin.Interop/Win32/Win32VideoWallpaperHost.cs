using System.Runtime.InteropServices;
using CosmicWin.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CosmicWin.Interop.Win32;

/// <summary>
/// Real, CsWin32-backed <see cref="IVideoWallpaperHost"/>. Ported faithfully from the proven
/// sequence in <c>spikes/AttachSpike/Program.cs</c> -- see that file and
/// <c>docs/research/video-wallpaper-feasibility.md</c> §3.6 for why every step is exactly what it
/// is (six other attach/paint approaches were tried and failed on this machine before this one).
/// </summary>
/// <remarks>
/// <para>
/// <c>TryAttach</c> is idempotent by design, not by accident: the first successful call creates the
/// host window, hidden top-level <c>TaskbarCreated</c> receiver, and D3D11 device/swapchain; every
/// later call re-runs the Progman/WorkerW/DefView discovery and parent/z-order half against the
/// window that already exists. The device and swapchain are kept once created -- Explorer
/// restarting invalidates the desktop's window tree, not this process's D3D state -- but a first
/// attach that creates/parents the HWND and then fails D3D creation can retry D3D on the next call.
/// </para>
/// <para>
/// The window is created <c>WS_POPUP</c>, not <c>WS_CHILD</c>: <c>CreateWindowEx</c> refuses
/// <c>WS_CHILD</c> with no parent handle yet (error 1406, <c>ERROR_TLW_WITH_WSCHILD</c>). It only
/// becomes <c>WS_CHILD</c> once <c>SetParent</c> has given it a real parent, exactly as the spike
/// measured.
/// </para>
/// </remarks>
public sealed unsafe class Win32VideoWallpaperHost : IVideoWallpaperHost
{
    private const string ClassName = "CosmicWinVideoWallpaperHost";

    /// <summary>
    /// Presented until a later task (Media Foundation frame-server playback) writes real video
    /// frames into the same render target -- proves presentation itself works, per this task's
    /// scope ("something presenting", not real video).
    /// </summary>
    private static readonly float[] TestPatternColor = [0f, 0f, 0f, 1f];

    private readonly string _className = $"{ClassName}-{Guid.NewGuid():N}";

    private WNDPROC? _wndProcDelegate;
    private uint _taskbarCreatedMessage;
    private bool _classRegistered;

    private HWND _hwnd;
    private HWND _taskbarMessageHwnd;
    private HINSTANCE _hInstance;
    private SafeHandle? _hInstanceSafe;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGISwapChain1? _swapChain;
    private ID3D11Texture2D? _backBuffer;
    private ID3D11RenderTargetView? _rtv;

    private bool _disposed;

    /// <summary>The real native handle, once <see cref="TryAttach"/> has created the window. Test-only observability -- deliberately not on <see cref="IVideoWallpaperHost"/>.</summary>
    internal nint Hwnd => (nint)_hwnd.Value;

    /// <summary>Hidden top-level message receiver that survives after the visible host becomes a child window.</summary>
    internal nint TaskbarMessageHwnd => (nint)_taskbarMessageHwnd.Value;

    // Internal, matching IVideoWallpaperHost's own internal Device/GetBackBuffer members -- see
    // that interface's remarks for why (CsWin32's D3D types are internal to this assembly, and a
    // public member cannot return a less-accessible type). Kept as ordinary internal members (not
    // only explicit interface implementations) because Win32VideoWallpaperHostRealAttachTests
    // calls GetBackBuffer() directly on the concrete type, not through the interface.
    internal ID3D11Device Device =>
        _device ?? throw new InvalidOperationException("TryAttach must succeed before Device is available.");

    internal ID3D11Texture2D GetBackBuffer() =>
        _backBuffer ?? throw new InvalidOperationException("TryAttach must succeed before a back buffer is available.");

    // Explicit interface implementations forwarding to the members above: a non-public interface
    // member (Device/GetBackBuffer are `internal` on IVideoWallpaperHost) cannot be satisfied
    // implicitly -- C# requires an explicit implementation for it (CS0737).
    ID3D11Device IVideoWallpaperHost.Device => Device;

    ID3D11Texture2D IVideoWallpaperHost.GetBackBuffer() => GetBackBuffer();

    public bool TryAttach()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            if (_taskbarCreatedMessage == 0)
            {
                _taskbarCreatedMessage = PInvoke.RegisterWindowMessage("TaskbarCreated");
                if (_taskbarCreatedMessage == 0)
                {
                    return false;
                }
            }

            if (_hwnd.IsNull)
            {
                return CreateAndAttach() && EnsureTaskbarMessageWindow() && IsD3DReady();
            }

            if (!EnsureTaskbarMessageWindow())
            {
                return false;
            }

            if (!AttachToDesktop(_hwnd))
            {
                return false;
            }

            return IsD3DReady() || CreateSwapChainAndPresentTestPattern(_hwnd);
        }
        catch
        {
            // Interface contract: TryAttach never throws, it reports failure. A partially
            // attached window/device from a failed first attempt is left in place rather than
            // torn down -- Dispose() is the only place that tears down, so a caller that retries
            // TryAttach later does not lose whatever succeeded so far.
            return false;
        }
    }

    public void Present()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_swapChain is null)
        {
            throw new InvalidOperationException("TryAttach must succeed before Present can be called.");
        }

        // No clear here: a caller (e.g. the T4 Media Foundation frame-server player) may have
        // already written real content into the back buffer via TransferVideoFrame, and clearing
        // unconditionally on every Present would wipe it out. The one-time test-pattern clear that
        // proves presentation itself works happens once, directly, in
        // CreateSwapChainAndPresentTestPattern -- before this method is ever reachable from it.
        _swapChain.Present(1, 0);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        ReleaseD3DResources();

        if (!_hwnd.IsNull)
        {
            PInvoke.DestroyWindow(_hwnd);
            _hwnd = default;
        }

        if (!_taskbarMessageHwnd.IsNull)
        {
            PInvoke.DestroyWindow(_taskbarMessageHwnd);
            _taskbarMessageHwnd = default;
        }

        if (_classRegistered)
        {
            PInvoke.UnregisterClass(_className, _hInstanceSafe!);
            _classRegistered = false;
            _hInstanceSafe = null;
            _hInstance = default;
        }
    }

    /// <summary>First-ever attach: creates the window, then attaches it, then creates D3D.</summary>
    private bool CreateAndAttach()
    {
        var childHwnd = CreateHostWindow();
        if (childHwnd.IsNull)
        {
            return false;
        }

        _hwnd = childHwnd;

        if (!AttachToDesktop(childHwnd))
        {
            PInvoke.DestroyWindow(childHwnd);
            _hwnd = default;
            return false;
        }

        return CreateSwapChainAndPresentTestPattern(childHwnd);
    }

    /// <summary>
    /// The Progman/WorkerW/DefView discovery, parent and z-order dance -- steps 1-7 from the task,
    /// shared between the first attach and every <c>TaskbarCreated</c> re-attach.
    /// </summary>
    private bool AttachToDesktop(HWND childHwnd)
    {
        HWND progman = PInvoke.FindWindow(null, "Program Manager");
        if (progman.IsNull)
        {
            return false;
        }

        // Plain (non-Ptr) GetWindowLong, not GetWindowLongPtr -- the Ptr variant is not
        // generatable for an AnyCPU target (PInvoke005), and GWL_EXSTYLE/GWL_STYLE are always
        // 32-bit values regardless of pointer width. Same convention as
        // Win32NativeWindowSource.ReadStyle/ReadExStyle.
        var exStyle = unchecked((uint)PInvoke.GetWindowLong(progman, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE));
        var raisedDesktop = DesktopLayoutDetector.IsRaisedDesktop(exStyle);

        // Single message, exactly once. The old two-message form (0xD,1) then (0xD,0) deletes the
        // new WorkerW instead of creating a durable one.
        nuint sendResult;
        PInvoke.SendMessageTimeout(
            progman,
            0x052C,
            0xD,
            0x1,
            SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_NORMAL,
            1000,
            &sendResult);

        HWND host;
        if (raisedDesktop)
        {
            // WorkerW is located for parity with the measured sequence but Progman is the actual
            // parent on this layout -- matches the spike's resolved (non-alternate) path.
            _ = PInvoke.FindWindowEx(progman, HWND.Null, "WorkerW", null);
            host = progman;
        }
        else
        {
            HWND ownerOfDefView = FindTopLevelOwningDefView();
            if (ownerOfDefView.IsNull)
            {
                return false;
            }

            HWND workerW = PInvoke.FindWindowEx(HWND.Null, ownerOfDefView, "WorkerW", null);
            if (workerW.IsNull)
            {
                return false;
            }

            host = workerW;
        }

        if (host.IsNull)
        {
            return false;
        }

        HWND existingParent = PInvoke.GetParent(childHwnd);
        if (existingParent == host)
        {
            return true;
        }

        PInvoke.SetParent(childHwnd, host);

        var newStyle = unchecked((int)((uint)WINDOW_STYLE.WS_CHILD | (uint)WINDOW_STYLE.WS_VISIBLE));
        PInvoke.SetWindowLong(childHwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE, newStyle);
        if (!PInvoke.SetWindowPos(
            childHwnd, HWND.Null, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE
            | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED))
        {
            return false;
        }

        var appliedStyle = unchecked((uint)PInvoke.GetWindowLong(childHwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE));
        if ((appliedStyle & (uint)WINDOW_STYLE.WS_CHILD) == 0)
        {
            return false;
        }

        if (PInvoke.GetParent(childHwnd) != host)
        {
            return false;
        }

        // HWND_BOTTOM (1): a pseudo-handle CsWin32 does not project as a named constant, used as
        // the raw literal exactly as the spike does.
        HWND hwndBottom = new(new IntPtr(1));
        HWND defView = PInvoke.FindWindowEx(host, HWND.Null, "SHELLDLL_DefView", null);
        if (!PInvoke.SetWindowPos(
            childHwnd,
            defView.IsNull ? hwndBottom : defView,
            0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOMOVE
            | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW))
        {
            return false;
        }

        PInvoke.ShowWindow(childHwnd, SHOW_WINDOW_CMD.SW_SHOW);
        return true;
    }

    private bool CreateSwapChainAndPresentTestPattern(HWND hwnd)
    {
        if (IsD3DReady())
        {
            return true;
        }

        ReleaseD3DResources();

        if (!PInvoke.GetWindowRect(hwnd, out RECT screenRect))
        {
            return false;
        }

        var width = Math.Max(1, screenRect.right - screenRect.left);
        var height = Math.Max(1, screenRect.bottom - screenRect.top);

        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        IDXGIDevice? dxgiDevice = null;
        IDXGIAdapter? adapter = null;
        IDXGIFactory2? factory = null;
        IDXGISwapChain1? swapChain = null;
        ID3D11Texture2D? backBuffer = null;
        ID3D11RenderTargetView? rtv = null;

        try
        {
            HRESULT deviceHr = PInvoke.D3D11CreateDevice(
                null,
                D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
                HMODULE.Null,
                D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                null,
                0,
                PInvoke.D3D11_SDK_VERSION,
                out device,
                null,
                out context);
            if (deviceHr.Failed || device is null || context is null)
            {
                return false;
            }

            dxgiDevice = (IDXGIDevice)device;
            dxgiDevice.GetAdapter(out adapter);
            if (adapter is null)
            {
                return false;
            }

            adapter.GetParent(out factory);
            if (factory is null)
            {
                return false;
            }

            DXGI_SWAP_CHAIN_DESC1 desc = new()
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
                BufferUsage = DXGI_USAGE.DXGI_USAGE_RENDER_TARGET_OUTPUT,
                BufferCount = 2,
                SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_DISCARD,
                AlphaMode = DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_IGNORE,
            };

            factory.CreateSwapChainForHwnd(device, hwnd, &desc, null, null, out swapChain);
            if (swapChain is null)
            {
                return false;
            }

            swapChain.GetBuffer(0, out backBuffer);
            if (backBuffer is null)
            {
                return false;
            }

            device.CreateRenderTargetView(backBuffer, null, out rtv);
            if (rtv is null)
            {
                return false;
            }

            _device = device;
            _context = context;
            _swapChain = swapChain;
            _backBuffer = backBuffer;
            _rtv = rtv;

            device = null;
            context = null;
            swapChain = null;
            backBuffer = null;
            rtv = null;

            try
            {
                // One-time test-pattern clear, done directly here (not inside Present()) -- proves
                // presentation itself works without making every later Present() call clear over
                // whatever real content a caller (e.g. the Media Foundation frame-server player)
                // already wrote into the back buffer.
                _context.ClearRenderTargetView(_rtv, TestPatternColor);
                Present();
            }
            catch
            {
                ReleaseD3DResources();
                throw;
            }

            return true;
        }
        finally
        {
            ReleaseComObject(rtv);
            ReleaseComObject(backBuffer);
            ReleaseComObject(swapChain);
            ReleaseComObject(factory);
            ReleaseComObject(adapter);
            ReleaseComObject(dxgiDevice);
            ReleaseComObject(context);
            ReleaseComObject(device);
        }
    }

    private bool EnsureTaskbarMessageWindow()
    {
        if (!_taskbarMessageHwnd.IsNull)
        {
            return true;
        }

        if (!EnsureWindowClassRegistered())
        {
            return false;
        }

        _taskbarMessageHwnd = PInvoke.CreateWindowEx(
            WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE,
            _className,
            "CosmicWin TaskbarCreated Receiver",
            WINDOW_STYLE.WS_POPUP,
            0,
            0,
            1,
            1,
            HWND.Null,
            null,
            _hInstanceSafe!,
            null);

        return !_taskbarMessageHwnd.IsNull;
    }

    private HWND CreateHostWindow()
    {
        if (!EnsureWindowClassRegistered())
        {
            return HWND.Null;
        }

        RECT workArea = GetPrimaryWorkArea();
        var width = workArea.right - workArea.left;
        var height = workArea.bottom - workArea.top;

        // No WS_EX_LAYERED: a DXGI flip-model swapchain presents through DWM directly and never
        // touches the GDI-era layered/UpdateLayeredWindow path.
        return PInvoke.CreateWindowEx(
            0,
            _className,
            "CosmicWin Video Wallpaper",
            WINDOW_STYLE.WS_POPUP | WINDOW_STYLE.WS_VISIBLE,
            workArea.left,
            workArea.top,
            width,
            height,
            HWND.Null,
            null,
            _hInstanceSafe!,
            null);
    }

    private bool EnsureWindowClassRegistered()
    {
        if (_classRegistered)
        {
            return true;
        }

        _wndProcDelegate ??= WndProc;

        // One handle, reused for both RegisterClassEx and CreateWindowEx: CreateWindowEx fails
        // with 1407 (ERROR_CANNOT_FIND_WND_CLASS) if its hInstance does not exactly match the one
        // the class was registered under, even though the class registered fine.
        _hInstanceSafe = PInvoke.GetModuleHandle((string?)null);
        _hInstance = new HINSTANCE(_hInstanceSafe.DangerousGetHandle());

        fixed (char* classNamePtr = _className)
        {
            WNDCLASSEXW wc = new()
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = _wndProcDelegate,
                hInstance = _hInstance,
                lpszClassName = classNamePtr,
                hCursor = PInvoke.LoadCursor(HINSTANCE.Null, PInvoke.IDC_ARROW),
            };

            if (PInvoke.RegisterClassEx(wc) == 0)
            {
                return false;
            }
        }

        _classRegistered = true;
        return true;
    }

    private static RECT GetPrimaryWorkArea()
    {
        HMONITOR primary = PInvoke.MonitorFromWindow(HWND.Null, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);
        MONITORINFO mi = new() { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (PInvoke.GetMonitorInfo(primary, ref mi))
        {
            return mi.rcWork;
        }

        return new RECT { left = 0, top = 0, right = 1920, bottom = 1080 };
    }

    private static HWND FindTopLevelOwningDefView()
    {
        HWND found = HWND.Null;
        PInvoke.EnumWindows(
            (hwnd, _) =>
            {
                HWND defView = PInvoke.FindWindowEx(hwnd, HWND.Null, "SHELLDLL_DefView", null);
                if (!defView.IsNull)
                {
                    found = hwnd;
                    return false;
                }

                return true;
            },
            IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// Handles the registered <c>TaskbarCreated</c> message (Explorer restarted) on the hidden
    /// top-level receiver by re-running the attach dance against this same host; everything else
    /// falls through to <c>DefWindowProc</c>. Deliberately never handles <c>WM_PAINT</c> -- the constraint in
    /// <c>odd/tasks/video-wallpaper.md</c> forbids a GDI fallback, and leaving <c>WM_PAINT</c>
    /// unhandled means <c>DefWindowProc</c>'s own default (no drawing) is what happens, not a
    /// silently-reintroduced fill.
    /// </summary>
    private LRESULT WndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        if (_taskbarCreatedMessage != 0 && msg == _taskbarCreatedMessage)
        {
            if (!_disposed)
            {
                TryAttach();
            }

            return new LRESULT(0);
        }

        return msg switch
        {
            PInvoke.WM_DESTROY => new LRESULT(0),
            _ => PInvoke.DefWindowProc(hwnd, msg, wParam, lParam),
        };
    }

    private bool IsD3DReady() =>
        _device is not null &&
        _context is not null &&
        _swapChain is not null &&
        _backBuffer is not null &&
        _rtv is not null;

    private void ReleaseD3DResources()
    {
        ReleaseComObject(_rtv);
        ReleaseComObject(_backBuffer);
        ReleaseComObject(_swapChain);
        ReleaseComObject(_context);
        ReleaseComObject(_device);
        _rtv = null;
        _backBuffer = null;
        _swapChain = null;
        _context = null;
        _device = null;
    }

    private static void ReleaseComObject(object? comObject)
    {
        if (comObject is null)
        {
            return;
        }

        if (comObject is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
