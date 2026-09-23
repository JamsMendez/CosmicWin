using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using CosmicWin.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D10;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Com;

namespace CosmicWin.Interop.Win32;

/// <summary>
/// Real, CsWin32-backed <see cref="IVideoWallpaperPlayer"/>. Wraps <c>IMFMediaEngine</c> in
/// <b>frame-server mode</b>: its own <c>IMFDXGIDeviceManager</c> wrapping the host's existing
/// D3D11 device (<see cref="IVideoWallpaperHost.Device"/>), attribute <c>MF_MEDIA_ENGINE_DXGI_MANAGER</c>
/// set, attribute <c>MF_MEDIA_ENGINE_PLAYBACK_HWND</c> deliberately never set. A dedicated
/// background thread drives the whole engine lifecycle -- creation, ticking, and teardown.
/// </summary>
/// <remarks>
/// <para>
/// <b>Threading model (T4 fix, replacing the WM_TIMER pump).</b> The first draft ticked the engine
/// from a <c>WM_TIMER</c> on the same STA thread that owned a private message-only pump window --
/// after <c>CoInitializeEx(COINIT_APARTMENTTHREADED)</c>. That deadlocked intermittently on real
/// hardware (near-zero CPU, no exception, anywhere from 1 to dozens of ticks in). Research into
/// Microsoft's own "Media Foundation and COM" documentation confirms Media Foundation does not
/// marshal STA objects to its internal MTA work-queue threads, and <see cref="IMFMediaEngineNotify"/>
/// callbacks run on those MTA threads -- an STA thread blocked synchronously inside an MF call at
/// the wrong moment is a classic circular-wait COM deadlock. Microsoft's own <c>meplayer.cpp</c>
/// sample (Windows 8 SDK, <c>VCSamples</c>) does not tick from the UI thread at all: it runs the
/// whole per-frame loop on a dedicated worker thread. This class now matches that model: one
/// long-lived <see cref="Thread"/> (not a thread-pool task -- this needs a stable COM apartment for
/// its entire life, and a pool thread could be reused by unrelated work) does
/// <c>CoInitializeEx(COINIT_MULTITHREADED)</c>, engine creation, the tick loop, and teardown, all
/// inline, never touching a window or a message loop.
/// </para>
/// <para>
/// <see cref="TryPlay"/> stays synchronous from the caller's point of view: it starts the worker
/// thread and blocks (with a timeout) on a <see cref="ManualResetEventSlim"/> the thread signals
/// once setup (device manager, engine, source, play) has finished, carrying success/failure via
/// <see cref="SetupOutcome.Succeeded"/>. <see cref="StopPlaybackOnly"/> (restart, the public
/// <see cref="Stop"/>, and <see cref="Dispose"/>) signals a second event and joins the thread with
/// a timeout rather than risking an indefinite hang.
/// </para>
/// <para>
/// <see cref="IVideoWallpaperHost.Device"/>/<c>GetBackBuffer()</c>/<c>Present()</c> are now called
/// from this worker thread instead of the host's own creating thread. D3D11/DXGI COM interfaces are
/// free-threaded by design, so this is expected to be safe; as defense-in-depth per Microsoft's
/// "Supporting Direct3D 11 Video Decoding in Media Foundation" guidance (explicit "deadlock issues"
/// language), <see cref="ProtectDeviceForCrossThreadAccess"/> marks the shared device
/// multithread-protected via <c>ID3D10Multithread::SetMultithreadProtected</c> right after the
/// device manager is created.
/// </para>
/// <para>
/// What is reused unchanged from the original draft (and from <c>spikes/PlaybackSpike/Program.cs</c>
/// before it): the <c>MFStartup</c>/<c>MFShutdown</c> lifecycle shape, the
/// <see cref="IMFMediaEngineNotify"/> implementation, and the <c>Type.GetTypeFromCLSID</c> +
/// <c>Activator.CreateInstance</c> activation workaround for the <c>MFMediaEngineClassFactory</c>
/// coclass (CsWin32 does not project coclass activation). The <c>OnVideoStreamTick</c>
/// S_OK/S_FALSE distinction is still not recoverable through the CsWin32 binding (it projects as
/// void) -- <c>TransferVideoFrame</c> is still called unconditionally every tick, wrapped in
/// try/catch, unchanged from before; this is a correctness nice-to-have, not part of this fix.
/// </para>
/// </remarks>
public sealed unsafe class MediaFoundationVideoWallpaperPlayer : IVideoWallpaperPlayer
{
    // CLSID_MFMediaEngineClassFactory, from mfmediaengine.h / wine-mirror mfmediaengine.idl --
    // same GUID spikes/PlaybackSpike/Program.cs uses and cross-checked the same way.
    private static readonly Guid MfMediaEngineClassFactoryClsid = new("b44392da-499b-446b-a4cb-005fead0e6d5");

    private const int TickIntervalMs = 16;
    private static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StopJoinTimeout = TimeSpan.FromSeconds(5);

    private readonly IFrameOverlay _overlay;

    private Thread? _workerThread;
    private ManualResetEventSlim? _stopSignal;

    private volatile IMFMediaEngine? _engine;
    private volatile MediaEngineNotify? _notify;

    private bool _disposed;

    public MediaFoundationVideoWallpaperPlayer(IFrameOverlay? overlay = null)
    {
        _overlay = overlay ?? NoOpFrameOverlay.Instance;
    }

    /// <summary>Test-only observability: whether an engine is currently set up to be ticked.</summary>
    internal bool IsPlayingForTests => _engine is not null;

    /// <summary>
    /// Test-only observability: whether the current (or most recent) engine ever raised
    /// <c>MF_MEDIA_ENGINE_EVENT_ERROR</c> through the notify callback.
    /// </summary>
    internal bool ErrorObservedForTests => _notify?.ErrorObserved ?? false;

    public bool TryPlay(IVideoWallpaperHost host, string videoPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(videoPath);

        if (!File.Exists(videoPath))
        {
            return false;
        }

        try
        {
            // Restart semantics: tear down any previous worker thread/engine cleanly before
            // starting a new one.
            StopPlaybackOnly();

            using var setupSignal = new ManualResetEventSlim(false);
            var stopSignal = new ManualResetEventSlim(false);
            var outcome = new SetupOutcome();

            var thread = new Thread(() => RunPlaybackThread(host, videoPath, setupSignal, stopSignal, outcome))
            {
                IsBackground = true,
                Name = "CosmicWinVideoWallpaperPlayback",
            };
            thread.Start();

            if (!setupSignal.Wait(SetupTimeout) || !outcome.Succeeded)
            {
                // Setup failed, or timed out waiting for it -- ask the thread to stop (it may
                // still be mid-setup) and join it before reporting failure, so a failed TryPlay
                // never leaks a running thread. The thread disposes stopSignal itself as the very
                // last thing it does, on every exit path -- single owner, no double-dispose.
                stopSignal.Set();
                thread.Join(StopJoinTimeout);
                return false;
            }

            _workerThread = thread;
            _stopSignal = stopSignal;
            return true;
        }
        catch
        {
            // Interface contract: TryPlay never throws, it reports failure.
            StopPlaybackOnly();
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        StopPlaybackOnly();
    }

    /// <summary>
    /// The interface's own <c>Stop</c>: releases the file currently playing (if any) without
    /// retiring this player, so a caller that is about to overwrite that file on disk -- the tray
    /// re-pick closure in <c>AppComposition</c>, importing a new video onto the same fixed
    /// destination the engine may still hold open -- can safely do so once this returns.
    /// </summary>
    /// <remarks>
    /// Guarded by <see cref="_disposed"/> rather than throwing <see cref="ObjectDisposedException"/>
    /// like <see cref="TryPlay"/> does: the interface documents <c>Stop</c> as never throwing, and
    /// a player already disposed already has nothing playing, so the guard makes this call an
    /// honest no-op instead of a surprise exception a caller has to guard against separately.
    /// </remarks>
    public void Stop()
    {
        if (_disposed)
        {
            return;
        }

        StopPlaybackOnly();
    }

    /// <summary>
    /// Signals the current worker thread to stop and joins it (bounded, never indefinitely).
    /// Shared by <see cref="TryPlay"/> (restart), the public <see cref="Stop"/>, and <see
    /// cref="Dispose"/> (final teardown). The worker thread itself releases the engine/device-manager,
    /// calls <c>MFShutdown</c>/<c>CoUninitialize</c>, nulls <see cref="_engine"/>/<see cref="_notify"/>,
    /// and disposes the stop signal as the last things it does before exiting -- never from this
    /// (caller) thread, so no COM object is ever touched from a thread other than the one that
    /// created it.
    /// </summary>
    private void StopPlaybackOnly()
    {
        Thread? thread = _workerThread;
        ManualResetEventSlim? stopSignal = _stopSignal;

        _workerThread = null;
        _stopSignal = null;

        if (thread is null)
        {
            return;
        }

        stopSignal?.Set();

        try
        {
            // Best-effort -- Dispose()/restart must never hang indefinitely waiting for a stuck
            // worker thread. A timeout here is swallowed, not surfaced: there is no way to safely
            // recover a thread that refuses to exit short of leaking it as a background thread,
            // which IsBackground = true already guarantees won't block process shutdown either way.
            thread.Join(StopJoinTimeout);
        }
        catch
        {
        }
    }

    /// <summary>
    /// The entire per-playback lifecycle, run inline on one dedicated background thread: COM/MF
    /// startup, device manager, engine, source, then the tick loop, then teardown. Never touches a
    /// window or a message loop -- see the class remarks for why.
    /// </summary>
    private void RunPlaybackThread(
        IVideoWallpaperHost host,
        string videoPath,
        ManualResetEventSlim setupSignal,
        ManualResetEventSlim stopSignal,
        SetupOutcome outcome)
    {
        bool comInitialized = false;
        bool mfStarted = false;
        IMFDXGIDeviceManager? deviceManager = null;
        IMFMediaEngine? engine = null;
        MediaEngineNotify? notify = null;

        try
        {
            // MTA, not STA, and no window/message loop ever created on this thread -- this is the
            // actual fix, not just moving the same code to a different thread. See the class
            // remarks for the deadlock this replaces.
            HRESULT coHr = PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED);
            comInitialized = coHr.Succeeded || coHr.Value == unchecked((int)0x80010106);

            HRESULT mfHr = PInvoke.MFStartup(PInvoke.MF_VERSION, PInvoke.MFSTARTUP_FULL);
            if (mfHr.Failed)
            {
                return;
            }

            mfStarted = true;

            if (!TryCreateDeviceManager(host.Device, out deviceManager) || deviceManager is null)
            {
                return;
            }

            ProtectDeviceForCrossThreadAccess(host.Device);

            if (!TryCreateEngine(deviceManager, out engine, out notify) || engine is null || notify is null)
            {
                return;
            }

            if (!TrySetSourceAndPlay(engine, videoPath))
            {
                return;
            }

            _engine = engine;
            _notify = notify;
            outcome.Succeeded = true;
        }
        catch
        {
            // Interface contract: TryPlay never throws, it reports failure -- swallow here and
            // let the caller observe outcome.Succeeded == false.
        }
        finally
        {
            setupSignal.Set();
        }

        if (outcome.Succeeded)
        {
            try
            {
                while (!stopSignal.Wait(TickIntervalMs))
                {
                    Tick(engine!, host, _overlay);
                }
            }
            finally
            {
                _engine = null;
                _notify = null;
            }
        }

        CleanupEngineResources(engine, deviceManager, mfStarted, comInitialized);
        stopSignal.Dispose();
    }

    private static bool TryCreateDeviceManager(ID3D11Device device, out IMFDXGIDeviceManager? deviceManager)
    {
        deviceManager = null;

        HRESULT hr = PInvoke.MFCreateDXGIDeviceManager(out uint resetToken, out IMFDXGIDeviceManager manager);
        if (hr.Failed || manager is null)
        {
            return false;
        }

        try
        {
            // ResetDevice is projected as void -- it throws (COMException) on a failed HRESULT
            // rather than returning one, so a failure here is caught by the caller's try/catch.
            manager.ResetDevice(device, resetToken);
        }
        catch
        {
            ReleaseComObject(manager);
            return false;
        }

        deviceManager = manager;
        return true;
    }

    /// <summary>
    /// Defense-in-depth per Microsoft's "Supporting Direct3D 11 Video Decoding in Media
    /// Foundation" guidance (explicit "deadlock issues" language): marks the shared D3D11 device
    /// multithread-protected now that it is genuinely accessed from two threads (this worker
    /// thread, and whichever thread created <see cref="IVideoWallpaperHost"/>). D3D11/DXGI COM
    /// interfaces are free-threaded by design regardless, so a missing/failed
    /// <c>ID3D10Multithread</c> query here does not block playback -- it is belt-and-suspenders,
    /// not the fix for the STA deadlock this class exists to fix.
    /// </summary>
    private static void ProtectDeviceForCrossThreadAccess(ID3D11Device device)
    {
        try
        {
            var multithread = (ID3D10Multithread)device;
            try
            {
                multithread.SetMultithreadProtected(true);
            }
            finally
            {
                ReleaseComObject(multithread);
            }
        }
        catch
        {
            // Best-effort -- see remarks above.
        }
    }

    private static bool TryCreateEngine(
        IMFDXGIDeviceManager deviceManager, out IMFMediaEngine? engine, out MediaEngineNotify? notify)
    {
        engine = null;
        notify = null;

        HRESULT attrHr = PInvoke.MFCreateAttributes(out IMFAttributes attributes, 2);
        if (attrHr.Failed || attributes is null)
        {
            return false;
        }

        try
        {
            var localNotify = new MediaEngineNotify();
            attributes.SetUnknown(PInvoke.MF_MEDIA_ENGINE_CALLBACK, localNotify);
            attributes.SetUnknown(PInvoke.MF_MEDIA_ENGINE_DXGI_MANAGER, deviceManager);

            Type? factoryType = Type.GetTypeFromCLSID(MfMediaEngineClassFactoryClsid);
            if (factoryType is null)
            {
                return false;
            }

            object? factoryObj = Activator.CreateInstance(factoryType);
            if (factoryObj is not IMFMediaEngineClassFactory factory)
            {
                return false;
            }

            try
            {
                factory.CreateInstance(0, attributes, out IMFMediaEngine createdEngine);
                if (createdEngine is null)
                {
                    return false;
                }

                engine = createdEngine;
                notify = localNotify;
                return true;
            }
            finally
            {
                ReleaseComObject(factoryObj);
            }
        }
        finally
        {
            ReleaseComObject(attributes);
        }
    }

    private static bool TrySetSourceAndPlay(IMFMediaEngine engine, string videoPath)
    {
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

        engine.SetLoop(true);
        engine.SetMuted(true);
        engine.Play();
        return true;
    }

    private static void Tick(IMFMediaEngine engine, IVideoWallpaperHost host, IFrameOverlay overlay)
    {
        TickCore(
            overlay,
            onVideoStreamTick: () => engine.OnVideoStreamTick(out long _),
            getBackBuffer: host.GetBackBuffer,
            getDesc: backBuffer =>
            {
                backBuffer.GetDesc(out D3D11_TEXTURE2D_DESC desc);
                return desc;
            },
            transferVideoFrame: (backBuffer, destination) =>
            {
                RECT localDestination = destination;
                engine.TransferVideoFrame(backBuffer, null, &localDestination, null);
            },
            present: host.Present);
    }

    internal static void TickForTests(
        IFrameOverlay overlay,
        Action onVideoStreamTick,
        Func<ID3D11Texture2D> getBackBuffer,
        Func<ID3D11Texture2D, D3D11_TEXTURE2D_DESC> getDesc,
        Action<ID3D11Texture2D, RECT> transferVideoFrame,
        Action present)
    {
        TickCore(overlay, onVideoStreamTick, getBackBuffer, getDesc, transferVideoFrame, present);
    }

    private static void TickCore(
        IFrameOverlay overlay,
        Action onVideoStreamTick,
        Func<ID3D11Texture2D> getBackBuffer,
        Func<ID3D11Texture2D, D3D11_TEXTURE2D_DESC> getDesc,
        Action<ID3D11Texture2D, RECT> transferVideoFrame,
        Action present)
    {
        try
        {
            // IMFMediaEngine::OnVideoStreamTick is documented to return S_OK when a new frame is
            // ready and S_FALSE when it is not, but CsWin32 projects it as void (out long pPts),
            // collapsing both non-error HRESULTs the same way and throwing only on a genuine
            // failure -- so the S_OK/S_FALSE distinction is not observable through this binding.
            // TransferVideoFrame is called unconditionally every tick instead; it is a no-op (or
            // a cheap re-present of the current frame) when there is nothing new, and any failure
            // is caught below rather than crashing the pump.
            onVideoStreamTick();

            ID3D11Texture2D backBuffer = getBackBuffer();
            D3D11_TEXTURE2D_DESC desc = getDesc(backBuffer);

            RECT destRect = new() { left = 0, top = 0, right = (int)desc.Width, bottom = (int)desc.Height };
            transferVideoFrame(backBuffer, destRect);
            DrawOverlay(overlay, backBuffer, destRect);
            present();
        }
        catch
        {
            // The frame pump must never crash the process or tear down the host window -- a bad
            // tick is skipped and playback is retried on the next tick.
        }
    }

    private static void DrawOverlay(IFrameOverlay overlay, ID3D11Texture2D backBuffer, RECT destination)
    {
        try
        {
            overlay.Draw(backBuffer, destination);
        }
        catch
        {
            // Overlay failures are contained to this frame: a transferred video frame should still
            // be presented, and the next tick gets another chance to draw.
        }
    }

    private static void CleanupEngineResources(
        IMFMediaEngine? engine, IMFDXGIDeviceManager? deviceManager, bool mfStarted, bool comInitialized)
    {
        if (engine is not null)
        {
            try
            {
                engine.Shutdown();
            }
            catch
            {
                // Best-effort -- worker-thread teardown must never throw because Shutdown() did.
            }
        }

        ReleaseComObject(engine);
        ReleaseComObject(deviceManager);

        if (mfStarted)
        {
            PInvoke.MFShutdown();
        }

        if (comInitialized)
        {
            PInvoke.CoUninitialize();
        }
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

    private sealed class NoOpFrameOverlay : IFrameOverlay
    {
        public static readonly NoOpFrameOverlay Instance = new();

        private NoOpFrameOverlay()
        {
        }

        public void Draw(ID3D11Texture2D backBuffer, RECT destination)
        {
        }
    }

    /// <summary>Carries the worker thread's setup result back to <see cref="TryPlay"/> across the
    /// <c>setupSignal</c> handshake -- the event's Set()/Wait() pair already provides the memory
    /// barrier needed for a plain field here.</summary>
    private sealed class SetupOutcome
    {
        public bool Succeeded;
    }

    [ComVisible(true)]
    private sealed class MediaEngineNotify : IMFMediaEngineNotify
    {
        public volatile bool ErrorObserved;

        public void EventNotify(uint @event, nuint param1, uint param2)
        {
            if ((MF_MEDIA_ENGINE_EVENT)@event == MF_MEDIA_ENGINE_EVENT.MF_MEDIA_ENGINE_EVENT_ERROR)
            {
                ErrorObserved = true;
            }
        }
    }
}
