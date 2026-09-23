using CosmicWin.Interop.Win32;
using Windows.Win32.Graphics.Direct3D11;

namespace CosmicWin.Interop;

/// <summary>
/// Abstracts the native "video wallpaper host" window: a <c>WS_CHILD</c> window attached directly
/// behind the desktop icons (Progman/WorkerW/<c>SHELLDLL_DefView</c>), presenting through a D3D11
/// device + DXGI flip-model swapchain. GDI painting is never used and never will be -- Progman
/// carries <c>WS_EX_NOREDIRECTIONBITMAP</c> on this Windows build, and six independent
/// GDI-painted configurations rendered zero visible pixels; a DXGI swapchain worked on the first
/// try. See <c>docs/research/video-wallpaper-feasibility.md</c> §3.6.
/// </summary>
/// <remarks>
/// <see cref="Win32VideoWallpaperHost"/> is the real, CsWin32-backed implementation, ported from
/// <c>spikes/AttachSpike/Program.cs</c>; tests substitute an in-memory fake. Public (root
/// <c>CosmicWin.Interop</c> namespace), the same shape as <see cref="IWindowShownWatcher"/>/<see
/// cref="Win32WindowShownWatcher"/>: T6 needs <c>CosmicWin.App</c>'s <c>AppComposition</c> to
/// construct and hold the real implementation directly, and that assembly is not covered by this
/// project's <c>InternalsVisibleTo</c>.
/// <para>
/// <see cref="Device"/> and <see cref="GetBackBuffer"/> stay <see langword="internal"/> members on
/// this otherwise-public interface (a C# 11+ interface accessibility modifier -- the class stays
/// implicitly implementable within this assembly and by <c>InternalsVisibleTo</c> friends) rather
/// than public: their types are CsWin32-generated and internal to this assembly by default, so a
/// public member returning either would itself be a compile error (CS0050/CS0053), and going the
/// other way -- making the CsWin32 D3D surface public solution-wide -- would blow a hole in "only
/// CosmicWin.Interop touches Win32" (design D1/D8) for no reason: <c>AppComposition</c> never reads
/// either member, only <see cref="TryAttach"/>/<see cref="Present"/>, exactly like
/// <c>MediaFoundationVideoWallpaperPlayer</c> reads them from inside this same assembly.
/// </para>
/// </remarks>
public interface IVideoWallpaperHost : IDisposable
{
    /// <summary>
    /// Runs the attach sequence: on the first call, discovers Progman/WorkerW/DefView, creates the
    /// host window, hidden top-level <c>TaskbarCreated</c> receiver, parents and z-orders the host
    /// behind the icons, then creates the D3D11 device and swapchain and presents one test-pattern
    /// frame. On a later call (e.g. after <c>TaskbarCreated</c>, when Explorer has restarted) it
    /// re-runs the discovery/parent/z-order half against the already-created window and retries D3D
    /// creation if a previous attach created the HWND but did not leave usable D3D resources. If the
    /// host window itself was destroyed in the meantime (Explorer restarting tears down its Progman
    /// parent along with it), a brand-new window is created, attached, and given a brand-new
    /// swapchain -- on the SAME D3D11 device, which is never re-created once it exists, since a
    /// caller (e.g. the Media Foundation frame-server player) may have built other resources on it.
    /// Never throws; returns <see langword="false"/> on any failure (for example, Progman not found).
    /// </summary>
    bool TryAttach();

    /// <summary>
    /// The D3D11 device backing the swapchain. Only valid once <see cref="TryAttach"/> has
    /// succeeded at least once. Internal -- see the interface remarks.
    /// </summary>
    internal ID3D11Device Device { get; }

    /// <summary>
    /// The swapchain's current back buffer. Only valid once <see cref="TryAttach"/> has succeeded
    /// at least once. Internal -- see the interface remarks.
    /// </summary>
    internal ID3D11Texture2D GetBackBuffer();

    /// <summary>
    /// Presents the current back buffer as-is (no clear). A caller that wants to present its own
    /// content -- e.g. <c>MediaFoundationVideoWallpaperPlayer</c> writing decoded video frames via
    /// <c>TransferVideoFrame</c> -- writes into <see cref="GetBackBuffer"/> first, then calls this.
    /// The one-time test-pattern clear proving presentation itself works happens once, internally,
    /// during the first successful <see cref="TryAttach"/>.
    /// </summary>
    void Present();
}
