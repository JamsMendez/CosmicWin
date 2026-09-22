using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace AttachSpike;

internal static unsafe class Program
{
    private const string ClassName = "AttachSpikeMagentaWindow";
    private static WNDPROC? _wndProcDelegate;

    private static void Main(string[] args)
    {
        bool parentToWorkerW = args.Length > 0 && args[0] == "workerw";
        Console.WriteLine("AttachSpike — Progman/WorkerW attach measurement");
        Console.WriteLine();

        HWND progman = PInvoke.FindWindow(null, "Program Manager");
        if (progman.IsNull)
        {
            Console.WriteLine($"FindWindow(Progman) FAILED, GetLastError={Marshal.GetLastWin32Error()}");
            return;
        }
        Console.WriteLine($"Progman handle: {progman}");

        nint exStyle = GetWindowLongPtrWide(progman, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        bool raisedDesktop = ((uint)exStyle & (uint)WINDOW_EX_STYLE.WS_EX_NOREDIRECTIONBITMAP) != 0;
        Console.WriteLine($"Progman GWL_EXSTYLE=0x{exStyle:X}, WS_EX_NOREDIRECTIONBITMAP set = {raisedDesktop}");
        Console.WriteLine(raisedDesktop
            ? "-> This machine appears to use the 24H2+ RAISED desktop layout."
            : "-> This machine appears to use the LEGACY desktop layout.");
        Console.WriteLine();

        // Single message. The old two-message form (0xD,1) then (0xD,0) deletes the new WorkerW instead.
        Console.WriteLine("Sending 0x052C (wParam=0xD, lParam=0x1) to Progman, once...");
        nuint result;
        LRESULT sendResult = PInvoke.SendMessageTimeout(
            progman,
            0x052C,
            0xD,
            0x1,
            SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_NORMAL,
            1000,
            &result);
        Console.WriteLine($"SendMessageTimeout returned {sendResult.Value}, result out-param={result}, GetLastError={Marshal.GetLastWin32Error()}");
        Console.WriteLine();

        HWND host;
        HWND workerW = default;
        if (raisedDesktop)
        {
            workerW = PInvoke.FindWindowEx(progman, HWND.Null, "WorkerW", null);
            Console.WriteLine($"[raised path] FindWindowEx(Progman, WorkerW) = {workerW}");
            if (workerW.IsNull)
            {
                Console.WriteLine($"FAILED to locate WorkerW under Progman. GetLastError={Marshal.GetLastWin32Error()}");
            }
            host = parentToWorkerW ? workerW : progman;
            Console.WriteLine($"[raised path] parent target = {(parentToWorkerW ? "WorkerW (alternate test)" : "Progman (per Lively's resolved approach)")}");
        }
        else
        {
            HWND ownerOfDefView = FindTopLevelOwningDefView();
            Console.WriteLine($"[legacy path] top-level window owning SHELLDLL_DefView = {ownerOfDefView}");
            if (ownerOfDefView.IsNull)
            {
                Console.WriteLine("FAILED to find a window owning SHELLDLL_DefView.");
                return;
            }
            workerW = PInvoke.FindWindowEx(HWND.Null, ownerOfDefView, "WorkerW", null);
            Console.WriteLine($"[legacy path] sibling WorkerW after DefView owner = {workerW}");
            if (workerW.IsNull)
            {
                Console.WriteLine($"FAILED to locate sibling WorkerW. GetLastError={Marshal.GetLastWin32Error()}");
                return;
            }
            host = workerW;
            Console.WriteLine("[legacy path] parent target = sibling WorkerW");
        }
        Console.WriteLine();

        if (host.IsNull)
        {
            Console.WriteLine("No valid parent target resolved, aborting before window creation.");
            return;
        }

        HWND childHwnd = CreateMagentaChildWindow();
        if (childHwnd.IsNull)
        {
            Console.WriteLine($"CreateWindowEx FAILED, GetLastError={Marshal.GetLastWin32Error()}");
            return;
        }
        Console.WriteLine($"Created magenta window: {childHwnd}");
        Console.WriteLine();

        bool parentOk = PInvoke.SetParent(childHwnd, host) != HWND.Null || Marshal.GetLastWin32Error() == 0;
        Console.WriteLine($"SetParent(child, {(raisedDesktop ? "Progman" : "WorkerW")}) -> success heuristic={parentOk}, GetLastError={Marshal.GetLastWin32Error()}");

        // CreateWindowEx refuses WS_CHILD without a parent handle (error 1406, ERROR_TLW_WITH_WSCHILD),
        // so the window was created WS_POPUP and only becomes WS_CHILD now that it has a real parent.
        nint newStyle = ((nint)WINDOW_STYLE.WS_CHILD | (nint)WINDOW_STYLE.WS_VISIBLE);
        PInvoke.SetWindowLongPtr(childHwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE, newStyle);
        PInvoke.SetWindowPos(childHwnd, HWND.Null, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED);

        HWND defView = PInvoke.FindWindowEx(host, HWND.Null, "SHELLDLL_DefView", null);
        Console.WriteLine($"SHELLDLL_DefView under host = {defView}");

        HWND hwndBottom = new(new IntPtr(1));
        bool posOk = PInvoke.SetWindowPos(
            childHwnd,
            defView.IsNull ? hwndBottom : defView,
            0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
        Console.WriteLine($"SetWindowPos (place after {(defView.IsNull ? "HWND_BOTTOM" : "DefView")}) -> {posOk}, GetLastError={Marshal.GetLastWin32Error()}");

        PInvoke.ShowWindow(childHwnd, SHOW_WINDOW_CMD.SW_SHOW);

        Console.WriteLine();
        Console.WriteLine("--- Final diagnostics ---");
        nint finalStyle = PInvoke.GetWindowLongPtr(childHwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
        nint finalExStyle = PInvoke.GetWindowLongPtr(childHwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        Console.WriteLine($"Final GWL_STYLE=0x{finalStyle:X} (WS_VISIBLE=0x10000000 bit set: {((ulong)finalStyle & 0x10000000) != 0}, WS_CHILD=0x40000000 bit set: {((ulong)finalStyle & 0x40000000) != 0})");
        Console.WriteLine($"Final GWL_EXSTYLE=0x{finalExStyle:X}");
        PInvoke.GetWindowRect(childHwnd, out RECT screenRect);
        Console.WriteLine($"GetWindowRect (screen coords) = ({screenRect.left},{screenRect.top})-({screenRect.right},{screenRect.bottom})");
        HWND actualParent = PInvoke.GetParent(childHwnd);
        Console.WriteLine($"GetParent = {actualParent} (expected host = {host})");
        BOOL visible = PInvoke.IsWindowVisible(childHwnd);
        Console.WriteLine($"IsWindowVisible = {visible.Value != 0}");
        HWND topOfZOrder = PInvoke.GetTopWindow(host);
        Console.WriteLine($"GetTopWindow(host) = {topOfZOrder} (our window is {childHwnd})");

        uint cloaked = 0;
        HRESULT cloakHr = PInvoke.DwmGetWindowAttribute(childHwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(uint));
        Console.WriteLine($"DwmGetWindowAttribute(DWMWA_CLOAKED) -> hr=0x{cloakHr.Value:X8}, cloaked=0x{cloaked:X} (0=not cloaked, 1=app, 2=shell, 4=inherited)");
        Console.WriteLine("--- end diagnostics ---");

        Console.WriteLine();
        Console.WriteLine("Presenting magenta via a DXGI flip-model swapchain (not GDI)...");
        int presentWidth = screenRect.right - screenRect.left;
        int presentHeight = screenRect.bottom - screenRect.top;
        bool presented = PresentMagentaViaD3D(childHwnd, presentWidth, presentHeight);
        Console.WriteLine($"D3D present -> {presented}");

        Console.WriteLine();
        Console.WriteLine("If this worked, a solid magenta rectangle should now be visible behind your desktop icons.");
        Console.WriteLine("Press any key to detach and exit (the child window is destroyed with this process)...");

        RunMessageLoopUntilKeyPress();
    }

    private static bool PresentMagentaViaD3D(HWND hwnd, int width, int height)
    {
        HRESULT deviceHr = PInvoke.D3D11CreateDevice(
            null,
            D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
            HMODULE.Null,
            D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            null,
            0,
            PInvoke.D3D11_SDK_VERSION,
            out ID3D11Device? device,
            null,
            out ID3D11DeviceContext? _);
        Console.WriteLine($"D3D11CreateDevice -> 0x{deviceHr.Value:X8}");
        if (deviceHr.Failed || device is null)
        {
            return false;
        }

        var dxgiDevice = (IDXGIDevice)device;
        dxgiDevice.GetAdapter(out IDXGIAdapter adapter);
        adapter.GetParent(out IDXGIFactory2 factory);

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

        factory.CreateSwapChainForHwnd(device, hwnd, &desc, null, null, out IDXGISwapChain1 swapChain);
        Console.WriteLine("CreateSwapChainForHwnd succeeded.");

        swapChain.GetBuffer(0, out ID3D11Texture2D backBuffer);
        device.CreateRenderTargetView(backBuffer, null, out ID3D11RenderTargetView rtv);

        device.GetImmediateContext(out ID3D11DeviceContext context);
        float[] magenta = [1f, 0f, 1f, 1f];
        context.ClearRenderTargetView(rtv, magenta);
        swapChain.Present(1, 0);
        Console.WriteLine("Present() called — magenta cleared and presented.");
        return true;
    }

    private static HWND FindTopLevelOwningDefView()
    {
        HWND found = HWND.Null;
        PInvoke.EnumWindows((hwnd, _) =>
        {
            HWND defView = PInvoke.FindWindowEx(hwnd, HWND.Null, "SHELLDLL_DefView", null);
            if (!defView.IsNull)
            {
                found = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static HWND CreateMagentaChildWindow()
    {
        _wndProcDelegate = WndProc;
        // One handle, reused for both calls: CreateWindowEx fails with 1407 (ERROR_CANNOT_FIND_WND_CLASS)
        // if its hInstance doesn't exactly match the one RegisterClassEx registered the class under.
        var hInstanceSafe = PInvoke.GetModuleHandle((string?)null);
        HINSTANCE hInstance = new(hInstanceSafe.DangerousGetHandle());

        fixed (char* classNamePtr = ClassName)
        {
            WNDCLASSEXW wc = new()
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = _wndProcDelegate,
                hInstance = hInstance,
                lpszClassName = classNamePtr,
                hbrBackground = PInvoke.CreateSolidBrush(new COLORREF(0x00FF00FF)),
                hCursor = PInvoke.LoadCursor(HINSTANCE.Null, PInvoke.IDC_ARROW),
            };

            ushort atom = PInvoke.RegisterClassEx(wc);
            if (atom == 0)
            {
                Console.WriteLine($"RegisterClassEx FAILED, GetLastError={Marshal.GetLastWin32Error()}");
                return HWND.Null;
            }
        }

        RECT workArea = GetPrimaryWorkArea();
        int width = workArea.right - workArea.left;
        int height = workArea.bottom - workArea.top;
        Console.WriteLine($"Primary work area: {width}x{height} at ({workArea.left},{workArea.top})");

        // No WS_EX_LAYERED this time: a DXGI flip-model swapchain presents through DWM directly,
        // it doesn't use the GDI-era layered/UpdateLayeredWindow path at all.
        return PInvoke.CreateWindowEx(
            0,
            ClassName,
            "AttachSpike",
            WINDOW_STYLE.WS_POPUP | WINDOW_STYLE.WS_VISIBLE,
            workArea.left,
            workArea.top,
            width,
            height,
            HWND.Null,
            null,
            hInstanceSafe,
            null);
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

    private static LRESULT WndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        switch (msg)
        {
            case PInvoke.WM_PAINT:
            {
                PAINTSTRUCT ps;
                HDC hdc = PInvoke.BeginPaint(hwnd, &ps);
                HBRUSH brush = PInvoke.CreateSolidBrush(new COLORREF(0x00FF00FF));
                PInvoke.FillRect(hdc, &ps.rcPaint, brush);
                PInvoke.DeleteObject(brush);
                PInvoke.EndPaint(hwnd, &ps);
                return new LRESULT(0);
            }
            case PInvoke.WM_DESTROY:
                PInvoke.PostQuitMessage(0);
                return new LRESULT(0);
            default:
                return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    private static void RunMessageLoopUntilKeyPress()
    {
        // Console.KeyAvailable throws when stdin isn't a real interactive console (e.g. redirected
        // output); fall back to a fixed run so the spike still ends on its own in that case.
        DateTime deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            bool keyPressed;
            try
            {
                keyPressed = Console.KeyAvailable;
            }
            catch (InvalidOperationException)
            {
                keyPressed = DateTime.UtcNow >= deadline;
            }
            if (keyPressed)
            {
                break;
            }

            while (PInvoke.PeekMessage(out MSG msg, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE))
            {
                PInvoke.TranslateMessage(msg);
                PInvoke.DispatchMessage(msg);
            }
            Thread.Sleep(15);
        }
        Console.WriteLine("Exiting — child window will be destroyed with this process.");
    }

    private static nint GetWindowLongPtrWide(HWND hwnd, WINDOW_LONG_PTR_INDEX index)
    {
        return PInvoke.GetWindowLongPtr(hwnd, index);
    }
}
