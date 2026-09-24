using System.Runtime.InteropServices;
using CosmicWin.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.DirectComposition;
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
/// window that already exists. The device is kept for the life of this object once created --
/// Explorer restarting invalidates the desktop's window tree (and, with it, the host window and
/// its window-bound swapchain), not this process's D3D device -- but a first attach that
/// creates/parents the HWND and then fails D3D creation can retry D3D on the next call.
/// </para>
/// <para>
/// R1 (explorer-restart-reattach): when Explorer restarts, the host window is a <c>WS_CHILD</c> of
/// Progman and is destroyed together with it, leaving <see cref="_hwnd"/> a dead handle. Rather than
/// retrying <see cref="AttachToDesktop"/> against that dead handle forever (what happened before
/// this fix), <c>TryAttach</c> detects it with <c>PInvoke.IsWindow</c>, drops the now-dead
/// window-bound swapchain/back buffer/render target view, creates and attaches a brand-new window,
/// and creates a brand-new swapchain for it -- on the SAME <see cref="_device"/>/<see cref="_context"/>,
/// never a new one, for the same reason the device is never re-created on an ordinary keep-alive
/// tick: <c>MediaFoundationVideoWallpaperPlayer</c> built its <c>IMFDXGIDeviceManager</c> on it once.
/// </para>
/// <para>
/// The window is created <c>WS_POPUP</c>, not <c>WS_CHILD</c>: <c>CreateWindowEx</c> refuses
/// <c>WS_CHILD</c> with no parent handle yet (error 1406, <c>ERROR_TLW_WITH_WSCHILD</c>). It only
/// becomes <c>WS_CHILD</c> once <c>SetParent</c> has given it a real parent, exactly as the spike
/// measured.
/// </para>
/// <para>
/// T2 (webview-alert-layer): the swapchain is always a DirectComposition one now (proven by the T0
/// spike, <c>spike/webview-alert-t0</c>, commits <c>b77366a</c>/<c>26fa64b</c>) -- a root visual with
/// the swapchain visual at the bottom, rebuilt in <see cref="RebuildCompositionTarget"/> whenever
/// the host window is (re)created, so a later caller (T3's WebView2 composition layer) can add ONE
/// overlay visual above the video via <see cref="AddCompositionOverlayVisual"/>. <b>Composition
/// threading</b>: DirectComposition objects are documented free-threaded (<c>IAgileObject</c>), but
/// their RCWs are apartment-bound -- T0 measured an RCW minted on this class's own thread (the
/// video-wallpaper thread) throwing <c>E_NOINTERFACE</c> when QI'd from the WPF UI (STA) thread that
/// calls <see cref="AddCompositionOverlayVisual"/>/<see cref="RemoveCompositionOverlayVisual"/>/
/// <see cref="CommitComposition"/>. The fix: every composition object that might be touched from a
/// different thread than the one that created it (device, root visual, swapchain visual, overlay
/// visual) is reduced to a raw <c>IUnknown</c> pointer the moment it is created, never cached as an
/// RCW; <see cref="CallerContextDComp{T}"/> mints a fresh RCW over that pointer for whichever thread
/// is calling right now, used for exactly one call and disposed before returning -- unlike the T0
/// spike, which minted the same wrapper once and intentionally leaked it forever.
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

    // T2 (webview-alert-layer): raw IUnknown pointers, not cached RCWs -- see the class remarks
    // "Composition threading". _dcompDevicePtr mirrors _device/_context: built once (in
    // EnsureCompositionDevice) and kept for the life of this object. _dcompTargetPtr/
    // _dcompRootVisualPtr/_dcompSwapChainVisualPtr mirror _swapChain/_backBuffer/_rtv: window-bound,
    // dropped and rebuilt together with them in RebuildCompositionTarget. _dcompOverlayVisualPtr is
    // the ONE overlay visual a caller (T3) has added via AddCompositionOverlayVisual, if any -- it
    // belongs to the window-bound tree above it and is dropped (never carried over) on a rebuild.
    private nint _dcompDevicePtr;
    private nint _dcompTargetPtr;
    private nint _dcompRootVisualPtr;
    private nint _dcompSwapChainVisualPtr;
    private nint _dcompOverlayVisualPtr;

    private bool _disposed;

    /// <summary>
    /// The real native handle, once <see cref="TryAttach"/> has created the window. Public (not on
    /// <see cref="IVideoWallpaperHost"/>, same reasoning as <see cref="AddCompositionOverlayVisual"/>
    /// below): T3's WebView2 composition layer needs it to create a
    /// <c>CoreWebView2CompositionController</c> against, from <c>CosmicWin.App</c> -- an assembly
    /// this one does not grant <c>InternalsVisibleTo</c>. Returns <c>nint</c>, never a CsWin32 type,
    /// for the same reason <see cref="AddCompositionOverlayVisual"/> returns <see cref="object"/>:
    /// CsWin32's generated Win32 types are internal to this assembly by default (design D1/D8), and
    /// a public member returning one would itself be a compile error. Also used directly by tests.
    /// </summary>
    public nint Hwnd => (nint)_hwnd.Value;

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

            // R1 (explorer-restart-reattach): a non-null _hwnd that is no longer a real window
            // means Explorer destroyed it (it was WS_CHILD of Progman and died with it) -- the
            // stale handle would otherwise make every AttachToDesktop call below fail forever.
            // Checked before EnsureTaskbarMessageWindow/AttachToDesktop, which both assume _hwnd
            // is at least a live window even if not yet correctly parented.
            if (!PInvoke.IsWindow(_hwnd))
            {
                return RecreateDestroyedHostWindow() && EnsureTaskbarMessageWindow() && IsD3DReady();
            }

            if (!EnsureTaskbarMessageWindow())
            {
                return false;
            }

            if (!AttachToDesktop(_hwnd))
            {
                return false;
            }

            return EnsureSwapChain(_hwnd);
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

    /// <summary>
    /// True once the composition device AND a window-bound target/root/swapchain visual tree all
    /// exist for the CURRENT host window -- the earliest point at which
    /// <see cref="AddCompositionOverlayVisual"/> can succeed.
    /// </summary>
    public bool IsCompositionReady =>
        _dcompDevicePtr != 0 && _dcompTargetPtr != 0 && _dcompRootVisualPtr != 0
        && _dcompSwapChainVisualPtr != 0;

    /// <summary>
    /// Bumped every time the window-bound composition target/visual tree is (re)built by
    /// <see cref="RebuildCompositionTarget"/> -- once after the first successful attach, and again
    /// whenever the host window is recreated (Explorer restart). A caller holding an overlay visual
    /// from <see cref="AddCompositionOverlayVisual"/> must treat a changed generation as "that
    /// visual's tree is gone" (<see cref="RebuildCompositionTarget"/> already dropped it) and add a
    /// fresh one.
    /// </summary>
    public int CompositionGeneration { get; private set; }

    /// <summary>
    /// Adds ONE DirectComposition visual as a child of the root visual, directly above the video
    /// swapchain visual, commits, and returns it typed as <see cref="object"/> so a caller in another
    /// assembly (T3's WebView2 composition layer) can assign it straight to
    /// <c>CoreWebView2CompositionController.RootVisualTarget</c> -- itself typed <see cref="object"/>
    /// for exactly this reason -- without this assembly ever exposing a DirectComposition type across
    /// the boundary. The "clean interop seam" the task asks for, instead of an
    /// <c>InternalsVisibleTo</c> hack (design D1/D8: only <c>CosmicWin.Interop</c> touches Win32).
    /// </summary>
    /// <remarks>
    /// At most one overlay visual exists at a time: a second call replaces the first, exactly like
    /// this method's own <c>Remove</c> counterpart. Returns <see langword="null"/> when
    /// <see cref="IsCompositionReady"/> is false or on any DirectComposition failure -- a caller must
    /// treat a null return as "not ready yet, try again", never as an exception to catch (a DComp
    /// failure must never stop video playback, which this whole seam sits beside, not inside).
    /// Safe to call from a thread other than the one that built the composition tree -- see the class
    /// remarks "Composition threading" and <see cref="CallerContextDComp{T}"/>.
    /// </remarks>
    public object? AddCompositionOverlayVisual()
    {
        if (!IsCompositionReady)
        {
            return null;
        }

        try
        {
            RemoveOverlayVisualCore();

            using var device = new CallerContextDComp<IDCompositionDevice>(_dcompDevicePtr);
            using var root = new CallerContextDComp<IDCompositionVisual>(_dcompRootVisualPtr);
            using var below = new CallerContextDComp<IDCompositionVisual>(_dcompSwapChainVisualPtr);

            device.Value.CreateVisual(out IDCompositionVisual overlay);
            root.Value.AddVisual(overlay, true, below.Value);
            device.Value.Commit();

            // Tracked as a raw pointer (see the class remarks) so RemoveCompositionOverlayVisual and
            // a later rebuild can find and release it, even from a different thread than this call.
            // The AddRef inside GetIUnknownForObject is what keeps the object alive once the RCW
            // returned here (which the caller owns from this point on) is eventually released.
            _dcompOverlayVisualPtr = Marshal.GetIUnknownForObject(overlay);
            return overlay;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Removes the overlay visual added by <see cref="AddCompositionOverlayVisual"/>, if any, and
    /// commits. Idempotent and never throws -- safe to call when nothing was ever added, when the
    /// composition tree is gone (a rebuild already dropped it), or from a different thread than the
    /// one that added it.
    /// </summary>
    public void RemoveCompositionOverlayVisual()
    {
        try
        {
            RemoveOverlayVisualCore();
        }
        catch
        {
            // A DComp failure must never propagate into caller code -- see the class remarks.
        }
    }

    /// <summary>
    /// Commits pending DirectComposition changes -- a caller that sets
    /// <c>CoreWebView2CompositionController.RootVisualTarget</c> to the visual returned by
    /// <see cref="AddCompositionOverlayVisual"/> must call this afterwards or the change never
    /// reaches the screen (matches the T0 spike's <c>SpikeCommit</c>). A no-op, never throwing, when
    /// no composition device exists yet.
    /// </summary>
    public void CommitComposition()
    {
        if (_dcompDevicePtr == 0)
        {
            return;
        }

        try
        {
            using var device = new CallerContextDComp<IDCompositionDevice>(_dcompDevicePtr);
            device.Value.Commit();
        }
        catch
        {
            // A DComp failure must never propagate into caller code -- see the class remarks.
        }
    }

    private void RemoveOverlayVisualCore()
    {
        if (_dcompOverlayVisualPtr == 0)
        {
            return;
        }

        if (_dcompDevicePtr != 0 && _dcompRootVisualPtr != 0)
        {
            using var device = new CallerContextDComp<IDCompositionDevice>(_dcompDevicePtr);
            using var root = new CallerContextDComp<IDCompositionVisual>(_dcompRootVisualPtr);
            using var overlay = new CallerContextDComp<IDCompositionVisual>(_dcompOverlayVisualPtr);
            root.Value.RemoveVisual(overlay.Value);
            device.Value.Commit();
        }

        ReleaseRawPointer(ref _dcompOverlayVisualPtr);
    }

    /// <summary>
    /// Mints a COM RCW for <paramref name="ptr"/> in the CALLING thread's context, and releases it
    /// (via <see cref="ReleaseComObject"/>) when this wrapper is disposed -- never cached, never
    /// reused across a call. See the class remarks "Composition threading" for why: an RCW minted on
    /// one thread cannot be QI'd from another for one of these interfaces (<c>E_NOINTERFACE</c>,
    /// proven on hardware in the T0 spike), even though the underlying object is free-threaded.
    /// </summary>
    private readonly struct CallerContextDComp<T> : IDisposable
        where T : class
    {
        public T Value { get; }

        public CallerContextDComp(nint ptr)
        {
            Value = (T)Marshal.GetUniqueObjectForIUnknown(ptr);
        }

        public void Dispose() => ReleaseComObject(Value);
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
    /// R1 (explorer-restart-reattach): recovers from the host window being destroyed out from
    /// under this process -- Explorer restarting tears down its Progman parent, and the host is a
    /// <c>WS_CHILD</c> of it, so it dies too. The stale <see cref="_hwnd"/> is a dead handle, and
    /// every window-bound D3D resource (swapchain, back buffer, render target view) built for it is
    /// dead along with it -- but <see cref="_device"/>/<see cref="_context"/> are NOT: they outlive
    /// the window, and <c>MediaFoundationVideoWallpaperPlayer</c> built its
    /// <c>IMFDXGIDeviceManager</c> on the device once, so replacing it would break playback that has
    /// nothing to do with which window presents it. Creates a fresh window, attaches it, and gives
    /// it a fresh swapchain on the SAME device.
    /// </summary>
    private bool RecreateDestroyedHostWindow()
    {
        // Only the window-bound resources are dead, so only they are dropped -- ReleaseD3DResources
        // would also release _device/_context, which is exactly what must NOT happen here.
        ReleaseSwapChainResources();

        HWND newHwnd = CreateHostWindow();
        if (newHwnd.IsNull)
        {
            // _hwnd is left as the dead handle: the next TryAttach still sees !IsWindow(_hwnd) and
            // retries window creation from here again, same as any other failed step below.
            return false;
        }

        _hwnd = newHwnd;

        if (!AttachToDesktop(newHwnd))
        {
            // The new window is alive even though attaching it failed -- left in place (not
            // destroyed) so the NEXT TryAttach takes the ordinary "hwnd already alive" path and
            // retries AttachToDesktop against it, exactly like a first attach that parents
            // successfully but fails D3D creation is retried today.
            return false;
        }

        return EnsureSwapChain(newHwnd);
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

        // Resolved WITHOUT sending the spawn message below -- on the raised-desktop layout host is
        // Progman itself, which always exists, and on the legacy layout a WorkerW created by an
        // earlier attach is still there to be found. Only a genuinely first-ever attach (or one
        // after Explorer destroyed the legacy WorkerW) needs the message, so trying this first is
        // what lets the fast path below skip it entirely in steady state.
        HWND host = ResolveHost(progman, raisedDesktop);

        // T3 (video-wallpaper-repick-and-slideshow): the fast path. `!host.IsNull` guards the one
        // edge case that resolving BEFORE the spawn message opens up -- on a legacy-layout machine
        // that has never attached, ResolveHost above returns HWND.Null because the WorkerW does not
        // exist yet, and GetParent(childHwnd) is also HWND.Null on a freshly created window; without
        // this guard the two nulls would compare equal and this would report success having attached
        // nothing.
        //
        // Checked, and returned from, BEFORE the 0x052C message farther down: that message is only
        // ever needed to make Explorer (re)create a worker window, and in the already-attached
        // steady state -- which is what the 400ms keep-alive tick in AppComposition hits on every
        // call once a video wallpaper is playing -- nothing needs creating. Sending it anyway, every
        // 400ms, for the life of the session would be an unresearched action against Explorer for a
        // path that never needs one.
        if (!host.IsNull && PInvoke.GetParent(childHwnd) == host)
        {
            return EnsureDirectlyAfterDefView(childHwnd, host);
        }

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

        // Re-resolved: on the legacy layout the message above may have just made Explorer create
        // the WorkerW that a first attach needs, which the pre-message resolve necessarily missed.
        // The raised-desktop layout's host is Progman itself and never depended on this message; the
        // re-resolve there just repeats a cheap, side-effect-free lookup.
        host = ResolveHost(progman, raisedDesktop);
        if (host.IsNull)
        {
            return false;
        }

        HWND existingParent = PInvoke.GetParent(childHwnd);
        if (existingParent == host)
        {
            return EnsureDirectlyAfterDefView(childHwnd, host);
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

    /// <summary>
    /// Finds the same host <see cref="AttachToDesktop"/> has always targeted, WITHOUT the
    /// <c>0x052C</c> spawn message: Progman itself on the raised-desktop layout (always exists), or
    /// the legacy layout's WorkerW sibling of DefView's owner (exists once something has attached
    /// before). Returns <see cref="HWND.Null"/> when that WorkerW does not exist yet -- a caller
    /// that needs one created still has to send the message and resolve again.
    /// </summary>
    private static HWND ResolveHost(HWND progman, bool raisedDesktop)
    {
        if (raisedDesktop)
        {
            // WorkerW is located for parity with the measured sequence but Progman is the actual
            // parent on this layout -- matches the spike's resolved (non-alternate) path.
            _ = PInvoke.FindWindowEx(progman, HWND.Null, "WorkerW", null);
            return progman;
        }

        HWND ownerOfDefView = FindTopLevelOwningDefView();
        if (ownerOfDefView.IsNull)
        {
            return HWND.Null;
        }

        return PInvoke.FindWindowEx(HWND.Null, ownerOfDefView, "WorkerW", null);
    }

    /// <summary>
    /// T3 (video-wallpaper-repick-and-slideshow): the parent matching <paramref name="host"/> alone
    /// used to be enough for <see cref="AttachToDesktop"/> to report success outright, and that was
    /// exactly the bug T2 proved live on hardware -- the Windows wallpaper slideshow creates a NEW
    /// wallpaper WorkerW, inserts it directly after <c>SHELLDLL_DefView</c> (i.e. directly ABOVE the
    /// host), then destroys the old one, and this early return kept even a re-attach from ever
    /// noticing. The parent match is now necessary but not sufficient: the host must also still sit
    /// directly after DefView, and if it does not, only the z-order is re-applied here -- no
    /// re-parent, no style change, since both of those already hold and touching them again would be
    /// wasted Win32 calls against a window that is already correctly parented and styled.
    /// </summary>
    /// <remarks>
    /// When there is no DefView under <paramref name="host"/> at all -- the legacy WorkerW layout,
    /// where DefView lives under a different top-level owner entirely (see
    /// <see cref="FindTopLevelOwningDefView"/>), never under the WorkerW host itself -- there is
    /// nothing here to compare the order against, so the original "parent already matches" signal is
    /// trusted exactly as it always was.
    /// </remarks>
    private static bool EnsureDirectlyAfterDefView(HWND childHwnd, HWND host)
    {
        HWND defView = PInvoke.FindWindowEx(host, HWND.Null, "SHELLDLL_DefView", null);
        if (defView.IsNull)
        {
            return true;
        }

        if (PInvoke.GetWindow(defView, GET_WINDOW_CMD.GW_HWNDNEXT) == childHwnd)
        {
            // Already exactly where the slideshow keep-alive tick wants us: a true no-op steady
            // state, so no SetWindowPos call happens here -- the whole reason this fast path exists
            // is for that steady state to cost nothing.
            return true;
        }

        // Explorer's slideshow replaced the wallpaper WorkerW/DefView sibling with a fresh one
        // inserted directly after DefView, i.e. above us (T2's proven repro). The parent is still
        // right, so only the z-order needs to be re-applied.
        return PInvoke.SetWindowPos(
            childHwnd,
            defView,
            0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOMOVE
            | SET_WINDOW_POS_FLAGS.SWP_NOSIZE);
    }

    /// <summary>
    /// Creates the D3D11 device, context, AND swapchain together, exactly as the very first
    /// successful attach always has -- <see cref="ReleaseD3DResources"/> below releases the device
    /// too, so this is only ever correct when no device exists yet to preserve (a genuine
    /// first-ever attach, or a first attach whose D3D creation previously failed outright). A
    /// reattach that must keep an existing device alive uses <see cref="CreateSwapChainOnExistingDevice"/>
    /// instead -- see <see cref="EnsureSwapChain"/>, which picks between the two.
    /// </summary>
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
                // T2 (webview-alert-layer): CreateSwapChainForComposition requires FLIP_SEQUENTIAL,
                // not FLIP_DISCARD -- proven in the T0 spike. Scaling is left at its default
                // (STRETCH, value 0), the only value that API accepts.
                SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL,
                AlphaMode = DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_IGNORE,
            };

            // T2: no HWND -- a composition swapchain shows nothing at all until a DirectComposition
            // target/visual tree references it and is committed (RebuildCompositionTarget below).
            factory.CreateSwapChainForComposition(device, &desc, null, out swapChain);
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

            if (!EnsureCompositionDevice(dxgiDevice!) || !RebuildCompositionTarget(hwnd))
            {
                ReleaseD3DResources();
                return false;
            }

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

    /// <summary>
    /// Ensures a ready swapchain for <paramref name="hwnd"/>, preserving <see cref="_device"/> and
    /// <see cref="_context"/> when they already exist rather than re-creating them: picks
    /// <see cref="CreateSwapChainOnExistingDevice"/> when a device survives from an earlier attach
    /// (the ordinary keep-alive/TaskbarCreated retry, and R1's destroyed-window recovery), or falls
    /// back to <see cref="CreateSwapChainAndPresentTestPattern"/> -- full device + swapchain
    /// creation -- for the rare case where the window exists but D3D creation never succeeded at
    /// all (nothing to preserve yet).
    /// </summary>
    private bool EnsureSwapChain(HWND hwnd)
    {
        if (IsD3DReady())
        {
            return true;
        }

        return _device is not null && _context is not null
            ? CreateSwapChainOnExistingDevice(hwnd)
            : CreateSwapChainAndPresentTestPattern(hwnd);
    }

    /// <summary>
    /// R1 (explorer-restart-reattach): the swapchain half of <see cref="CreateSwapChainAndPresentTestPattern"/>,
    /// split out to run against an ALREADY-EXISTING <see cref="_device"/>/<see cref="_context"/>
    /// instead of creating a new one -- caller (<see cref="EnsureSwapChain"/>) guarantees both are
    /// non-null. Used when the host window was recreated (the D3D device is untouched by a window
    /// being destroyed) so <c>MediaFoundationVideoWallpaperPlayer</c>'s <c>IMFDXGIDeviceManager</c>,
    /// built on the device once in <c>TryPlay</c>, never has to be rebuilt.
    /// </summary>
    /// <remarks>
    /// Concurrency: <see cref="_swapChain"/>/<see cref="_backBuffer"/>/<see cref="_rtv"/> are plain
    /// fields, written here from the video-wallpaper thread while the player's own worker thread may
    /// concurrently read them via <see cref="GetBackBuffer"/>/<see cref="Present"/> every tick. This
    /// is no different from what <see cref="CreateSwapChainAndPresentTestPattern"/> already does on
    /// every ordinary (re)attach today: the fields are released (nulled) before the new ones are
    /// created, so a tick landing in that window sees either the old, still-valid resources or a
    /// null that <see cref="GetBackBuffer"/>/<see cref="Present"/> already turn into an
    /// <see cref="InvalidOperationException"/> -- which <c>MediaFoundationVideoWallpaperPlayer.Tick</c>
    /// already catches and swallows as "a bad tick, retried next time". No new failure mode is
    /// introduced; D3D11/DXGI COM interfaces are free-threaded by design regardless (see
    /// <c>MediaFoundationVideoWallpaperPlayer</c>'s own remarks).
    /// </remarks>
    private bool CreateSwapChainOnExistingDevice(HWND hwnd)
    {
        var device = _device!;
        var context = _context!;

        ReleaseSwapChainResources();

        if (!PInvoke.GetWindowRect(hwnd, out RECT screenRect))
        {
            return false;
        }

        var width = Math.Max(1, screenRect.right - screenRect.left);
        var height = Math.Max(1, screenRect.bottom - screenRect.top);

        IDXGIDevice? dxgiDevice = null;
        IDXGIAdapter? adapter = null;
        IDXGIFactory2? factory = null;
        IDXGISwapChain1? swapChain = null;
        ID3D11Texture2D? backBuffer = null;
        ID3D11RenderTargetView? rtv = null;

        try
        {
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
                // T2 (webview-alert-layer): see the matching comment in
                // CreateSwapChainAndPresentTestPattern -- composition requires FLIP_SEQUENTIAL.
                SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL,
                AlphaMode = DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_IGNORE,
            };

            factory.CreateSwapChainForComposition(device, &desc, null, out swapChain);
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

            _swapChain = swapChain;
            _backBuffer = backBuffer;
            _rtv = rtv;

            swapChain = null;
            backBuffer = null;
            rtv = null;

            // T2: R1 (explorer-restart-reattach) rebuild path -- the OLD target/visual tree died
            // with the destroyed window; a fresh one is required for anything to show again.
            // _dcompDevicePtr itself is untouched (built once, like _device/_context).
            if (!EnsureCompositionDevice(dxgiDevice!) || !RebuildCompositionTarget(hwnd))
            {
                ReleaseSwapChainResources();
                return false;
            }

            try
            {
                // One-time test-pattern clear, same as the first-ever attach -- proves the NEW
                // swapchain presents before the player's next tick writes a real frame into it.
                context.ClearRenderTargetView(_rtv, TestPatternColor);
                Present();
            }
            catch
            {
                ReleaseSwapChainResources();
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
        }
    }

    /// <summary>
    /// Creates <see cref="_dcompDevicePtr"/> once from the DXGI device -- a no-op returning
    /// <see langword="true"/> when it already exists, mirroring how <see cref="_device"/> itself is
    /// never re-created for an already-attached host. Reduced to a raw pointer immediately (see the
    /// class remarks "Composition threading") rather than kept as a field.
    /// </summary>
    private bool EnsureCompositionDevice(IDXGIDevice dxgiDevice)
    {
        if (_dcompDevicePtr != 0)
        {
            return true;
        }

        IDCompositionDevice? dcompDevice = null;
        try
        {
            PInvoke.DCompositionCreateDevice(dxgiDevice, out dcompDevice);
            if (dcompDevice is null)
            {
                return false;
            }

            _dcompDevicePtr = Marshal.GetIUnknownForObject(dcompDevice);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            ReleaseComObject(dcompDevice);
        }
    }

    /// <summary>
    /// Builds (or rebuilds, after the host window was recreated) the DirectComposition target and
    /// visual tree for <paramref name="hwnd"/> -- root visual with the swapchain (video) visual as
    /// its only child so far, bound to the window via <see cref="IDCompositionTarget"/>, committed.
    /// A caller (T3) adds its own overlay visual above the swapchain one afterwards, via
    /// <see cref="AddCompositionOverlayVisual"/>. Window-bound, like <see cref="_swapChain"/>/
    /// <see cref="_backBuffer"/>/<see cref="_rtv"/> -- rebuilt fresh every time the window itself is
    /// fresh; <see cref="_dcompDevicePtr"/> is not, for the same reason <see cref="_device"/> is not
    /// (built once on the DXGI device, which survives the window dying). Bumps
    /// <see cref="CompositionGeneration"/> and drops any tracked overlay visual, since it belonged to
    /// the tree this just replaced.
    /// </summary>
    private bool RebuildCompositionTarget(HWND hwnd)
    {
        if (_dcompDevicePtr == 0 || _swapChain is null)
        {
            return false;
        }

        IDCompositionDevice? device = null;
        IDCompositionTarget? target = null;
        IDCompositionVisual? rootVisual = null;
        IDCompositionVisual? swapChainVisual = null;

        try
        {
            device = (IDCompositionDevice)Marshal.GetUniqueObjectForIUnknown(_dcompDevicePtr);
            device.CreateTargetForHwnd(hwnd, true, out target);
            device.CreateVisual(out rootVisual);
            device.CreateVisual(out swapChainVisual);
            swapChainVisual.SetContent(_swapChain);
            rootVisual.AddVisual(swapChainVisual, false, null);
            target!.SetRoot(rootVisual);
            device.Commit();

            ReleaseWindowBoundCompositionPointers();
            ReleaseRawPointer(ref _dcompOverlayVisualPtr);

            _dcompTargetPtr = Marshal.GetIUnknownForObject(target);
            _dcompRootVisualPtr = Marshal.GetIUnknownForObject(rootVisual);
            _dcompSwapChainVisualPtr = Marshal.GetIUnknownForObject(swapChainVisual);
            CompositionGeneration++;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            ReleaseComObject(swapChainVisual);
            ReleaseComObject(rootVisual);
            ReleaseComObject(target);
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

        // T11 (live-alert-wallpaper): the whole MONITOR, not the work area -- sizing to rcWork used
        // to letterbox an ultrawide video with black bars top and bottom on a monitor whose taskbar
        // is docked right (measured: 3440x1440 monitor, 3392x1440 work area). The swapchain is sized
        // from this same window's GetWindowRect further down (CreateSwapChainAndPresentTestPattern /
        // CreateSwapChainOnExistingDevice), so this one change is enough to size both -- the video
        // now runs under the taskbar too. The alert tile layout stays confined to the work area; see
        // AppComposition, which now maps its tiles into these same back-buffer coordinates.
        RECT monitorRect = GetPrimaryMonitorRect();
        var width = monitorRect.right - monitorRect.left;
        var height = monitorRect.bottom - monitorRect.top;

        // No WS_EX_LAYERED: a DXGI flip-model swapchain presents through DWM directly and never
        // touches the GDI-era layered/UpdateLayeredWindow path.
        return PInvoke.CreateWindowEx(
            0,
            _className,
            "CosmicWin Video Wallpaper",
            WINDOW_STYLE.WS_POPUP | WINDOW_STYLE.WS_VISIBLE,
            monitorRect.left,
            monitorRect.top,
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

    /// <summary>
    /// T11 (live-alert-wallpaper): <c>rcMonitor</c>, the WHOLE primary monitor -- renamed from the
    /// former <c>GetPrimaryWorkArea</c> (which returned <c>rcWork</c>) now that the host window
    /// spans the taskbar too. The alert overlay is the one caller that still needs the work area
    /// specifically (so tiles are not drawn under the taskbar); it reads that separately, from
    /// <c>IDisplay.WorkArea</c> in <c>AppComposition</c>, not from this host.
    /// </summary>
    private static RECT GetPrimaryMonitorRect()
    {
        HMONITOR primary = PInvoke.MonitorFromWindow(HWND.Null, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);
        MONITORINFO mi = new() { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (PInvoke.GetMonitorInfo(primary, ref mi))
        {
            return mi.rcMonitor;
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
        ReleaseSwapChainResources();
        ReleaseComObject(_context);
        ReleaseComObject(_device);
        // T2 (webview-alert-layer): released here, not in ReleaseSwapChainResources, for the same
        // reason _device is not -- built once on the DXGI device, survives a window recreate; see
        // RebuildCompositionTarget. A raw pointer (see the class remarks), so this is a direct
        // Release, not ReleaseComObject -- there is no RCW field to dispose.
        ReleaseRawPointer(ref _dcompDevicePtr);
        _context = null;
        _device = null;
    }

    /// <summary>
    /// Releases only the window-bound D3D resources (swapchain, back buffer, render target view) --
    /// <see cref="_device"/> and <see cref="_context"/> are left untouched. R1
    /// (explorer-restart-reattach): this is what <see cref="RecreateDestroyedHostWindow"/> and
    /// <see cref="CreateSwapChainOnExistingDevice"/> use instead of <see cref="ReleaseD3DResources"/>
    /// -- the whole point of recovering a destroyed host window is that the device must survive it.
    /// </summary>
    private void ReleaseSwapChainResources()
    {
        ReleaseComObject(_rtv);
        ReleaseComObject(_backBuffer);
        ReleaseComObject(_swapChain);
        // T2: window-bound, like the swapchain itself -- released and rebuilt together with it. The
        // overlay visual (if any) belonged to this same tree, so it goes too.
        ReleaseWindowBoundCompositionPointers();
        ReleaseRawPointer(ref _dcompOverlayVisualPtr);
        _rtv = null;
        _backBuffer = null;
        _swapChain = null;
    }

    private void ReleaseWindowBoundCompositionPointers()
    {
        ReleaseRawPointer(ref _dcompTargetPtr);
        ReleaseRawPointer(ref _dcompRootVisualPtr);
        ReleaseRawPointer(ref _dcompSwapChainVisualPtr);
    }

    /// <summary>
    /// Releases a raw <c>IUnknown</c> pointer captured by <see cref="Marshal.GetIUnknownForObject"/>
    /// directly, via <see cref="Marshal.Release"/> -- safe from ANY thread for a free-threaded
    /// (<c>IAgileObject</c>) COM object like every DirectComposition interface here, unlike calling a
    /// real method on it (see the class remarks "Composition threading"): <c>Release</c> is always
    /// vtable slot 2 on every COM interface, so no QueryInterface -- the actual failure point T0
    /// measured -- is ever needed just to drop a reference.
    /// </summary>
    private static void ReleaseRawPointer(ref nint ptr)
    {
        if (ptr != 0)
        {
            Marshal.Release(ptr);
            ptr = 0;
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
}
