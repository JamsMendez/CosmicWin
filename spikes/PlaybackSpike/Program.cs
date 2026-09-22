using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.WindowsAndMessaging;

namespace PlaybackSpike;

// CLSID_MFMediaEngineClassFactory, from mfmediaengine.h / wine-mirror mfmediaengine.idl.
// Not guessed: cross-checked against Microsoft Learn and wine's IDL mirror.
internal static class Clsid
{
    public static readonly Guid MFMediaEngineClassFactory = new("b44392da-499b-446b-a4cb-005fead0e6d5");
}

internal static unsafe class Program
{
    private const string ClassName = "PlaybackSpikeWindow";
    private static WNDPROC? _wndProcDelegate;
    private static bool _quit;

    private static void Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("Usage: dotnet run -- <path-to-video-file>");
            return;
        }

        string videoPath = Path.GetFullPath(args[0]);
        if (!File.Exists(videoPath))
        {
            Console.WriteLine($"File not found: {videoPath}");
            return;
        }

        Console.WriteLine("PlaybackSpike — IMFMediaEngine loop-seam measurement (CsWin32 managed bindings)");
        Console.WriteLine($"Video: {videoPath}");
        Console.WriteLine();

        HRESULT coHr = PInvoke.CoInitializeEx(null, COINIT.COINIT_APARTMENTTHREADED);
        Console.WriteLine($"CoInitializeEx -> 0x{coHr.Value:X8}");

        HRESULT mfHr = PInvoke.MFStartup(PInvoke.MF_VERSION, PInvoke.MFSTARTUP_FULL);
        Console.WriteLine($"MFStartup -> 0x{mfHr.Value:X8}");
        if (mfHr.Failed)
        {
            Console.WriteLine("MFStartup FAILED, aborting.");
            return;
        }

        HWND hwnd = CreateHostWindow();
        if (hwnd.IsNull)
        {
            Console.WriteLine("Failed to create host window, aborting.");
            return;
        }
        Console.WriteLine($"Host window: {hwnd}");
        PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_SHOW);

        var notify = new MediaEngineNotify();

        IMFMediaEngine? engine = CreateMediaEngine(hwnd, notify);
        if (engine is null)
        {
            Console.WriteLine("Failed to create IMFMediaEngine, aborting.");
            return;
        }
        Console.WriteLine("IMFMediaEngine created successfully.");

        string uri = new Uri(videoPath).AbsoluteUri;
        IntPtr bstrPtr = Marshal.StringToBSTR(uri);
        try
        {
            engine.SetSource(*(BSTR*)&bstrPtr);
        }
        finally
        {
            Marshal.FreeBSTR(bstrPtr);
        }
        Console.WriteLine("SetSource called.");

        engine.SetLoop(true);
        Console.WriteLine("SetLoop(true) called.");

        engine.Play();
        Console.WriteLine("Play() called.");

        Console.WriteLine();
        Console.WriteLine("Watch the window for a stutter/black-frame/freeze at each loop point.");
        Console.WriteLine("Each MF_MEDIA_ENGINE_EVENT_ENDED (loop restart) is logged with a timestamp below.");
        Console.WriteLine("Close the window to exit.");
        Console.WriteLine();

        // Poll HasVideo / GetNativeVideoSize once metadata should be loaded.
        _ = Task.Run(() => VideoInfoProbe.RunAfterDelay(engine));

        RunMessageLoop();

        Marshal.ReleaseComObject(engine);
        PInvoke.MFShutdown();
        PInvoke.CoUninitialize();
    }

    private static IMFMediaEngine? CreateMediaEngine(HWND hwnd, MediaEngineNotify notify)
    {
        HRESULT hr = PInvoke.MFCreateAttributes(out IMFAttributes attributes, 2);
        Console.WriteLine($"MFCreateAttributes -> 0x{hr.Value:X8}");
        if (hr.Failed)
        {
            return null;
        }

        attributes.SetUnknown(PInvoke.MF_MEDIA_ENGINE_CALLBACK, notify);
        Console.WriteLine("IMFAttributes::SetUnknown(MF_MEDIA_ENGINE_CALLBACK) set.");

        attributes.SetUINT64(PInvoke.MF_MEDIA_ENGINE_PLAYBACK_HWND, (ulong)hwnd.Value);
        Console.WriteLine("IMFAttributes::SetUINT64(MF_MEDIA_ENGINE_PLAYBACK_HWND) set.");

        Type? factoryType = Type.GetTypeFromCLSID(Clsid.MFMediaEngineClassFactory);
        object factoryObj = Activator.CreateInstance(factoryType!)!;
        var factory = (IMFMediaEngineClassFactory)factoryObj;

        factory.CreateInstance(0, attributes, out IMFMediaEngine engine);
        Console.WriteLine("IMFMediaEngineClassFactory::CreateInstance succeeded.");

        Marshal.ReleaseComObject(attributes);
        Marshal.ReleaseComObject(factory);
        return engine;
    }

    private static HWND CreateHostWindow()
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
                hbrBackground = new HBRUSH(PInvoke.GetStockObject(GET_STOCK_OBJECT_FLAGS.BLACK_BRUSH).Value),
                hCursor = PInvoke.LoadCursor(HINSTANCE.Null, PInvoke.IDC_ARROW),
            };

            ushort atom = PInvoke.RegisterClassEx(wc);
            if (atom == 0)
            {
                Console.WriteLine($"RegisterClassEx FAILED, GetLastError={Marshal.GetLastWin32Error()}");
                return HWND.Null;
            }
        }

        return PInvoke.CreateWindowEx(
            0,
            ClassName,
            "PlaybackSpike — watch for the loop seam",
            WINDOW_STYLE.WS_OVERLAPPEDWINDOW | WINDOW_STYLE.WS_VISIBLE,
            100, 100, 1280, 720,
            HWND.Null,
            null,
            hInstanceSafe,
            null);
    }

    private static LRESULT WndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        switch (msg)
        {
            case PInvoke.WM_CLOSE:
                PInvoke.DestroyWindow(hwnd);
                return new LRESULT(0);
            case PInvoke.WM_DESTROY:
                _quit = true;
                PInvoke.PostQuitMessage(0);
                return new LRESULT(0);
            default:
                return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    private static void RunMessageLoop()
    {
        while (!_quit && PInvoke.GetMessage(out MSG msg, HWND.Null, 0, 0))
        {
            PInvoke.TranslateMessage(msg);
            PInvoke.DispatchMessage(msg);
        }
    }
}

internal static class VideoInfoProbe
{
    public static async Task RunAfterDelay(IMFMediaEngine engine)
    {
        await Task.Delay(2000);
        try
        {
            bool hasVideo = engine.HasVideo();
            engine.GetNativeVideoSize(out uint w, out uint h);
            Console.WriteLine($"HasVideo()={hasVideo}, GetNativeVideoSize={w}x{h}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"HasVideo/GetNativeVideoSize probe failed: {ex.Message}");
        }
    }
}

[ComVisible(true)]
internal sealed class MediaEngineNotify : IMFMediaEngineNotify
{
    public void EventNotify(uint @event, nuint param1, uint param2)
    {
        var mediaEvent = (MF_MEDIA_ENGINE_EVENT)@event;
        switch (mediaEvent)
        {
            case MF_MEDIA_ENGINE_EVENT.MF_MEDIA_ENGINE_EVENT_ENDED:
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] MF_MEDIA_ENGINE_EVENT_ENDED — loop restart");
                break;
            case MF_MEDIA_ENGINE_EVENT.MF_MEDIA_ENGINE_EVENT_ERROR:
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] MF_MEDIA_ENGINE_EVENT_ERROR param1={param1} param2={param2}");
                break;
            case MF_MEDIA_ENGINE_EVENT.MF_MEDIA_ENGINE_EVENT_CANPLAY:
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] MF_MEDIA_ENGINE_EVENT_CANPLAY");
                break;
            case MF_MEDIA_ENGINE_EVENT.MF_MEDIA_ENGINE_EVENT_LOADEDMETADATA:
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] MF_MEDIA_ENGINE_EVENT_LOADEDMETADATA");
                break;
        }
    }
}
