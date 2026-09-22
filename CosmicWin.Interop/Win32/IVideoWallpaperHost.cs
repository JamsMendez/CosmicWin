using Windows.Win32.Graphics.Direct3D11;

namespace CosmicWin.Interop.Win32;

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
/// <c>spikes/AttachSpike/Program.cs</c>; tests substitute an in-memory fake. <see cref="Device"/>
/// is exposed so a later Media Foundation frame-server wrapper (a separate task) can reuse the same
/// D3D11 device rather than creating a second one.
/// </remarks>
internal interface IVideoWallpaperHost : IDisposable
{
    /// <summary>
    /// Runs the attach sequence: on the first call, discovers Progman/WorkerW/DefView, creates the
    /// host window, hidden top-level <c>TaskbarCreated</c> receiver, parents and z-orders the host
    /// behind the icons, then creates the D3D11 device and swapchain and presents one test-pattern
    /// frame. On a later call (e.g. after <c>TaskbarCreated</c>, when Explorer has restarted) it
    /// re-runs the discovery/parent/z-order half against the already-created window and retries D3D
    /// creation if a previous attach created the HWND but did not leave usable D3D resources. Never
    /// throws; returns <see langword="false"/> on any failure (for example, Progman not found).
    /// </summary>
    bool TryAttach();

    /// <summary>
    /// The D3D11 device backing the swapchain. Only valid once <see cref="TryAttach"/> has
    /// succeeded at least once.
    /// </summary>
    ID3D11Device Device { get; }

    /// <summary>
    /// The swapchain's current back buffer. Only valid once <see cref="TryAttach"/> has succeeded
    /// at least once.
    /// </summary>
    ID3D11Texture2D GetBackBuffer();

    /// <summary>
    /// Presents the current back buffer. Until real video frames are wired in by a later task, this
    /// clears to a fixed test-pattern colour first, so presentation itself is provably working.
    /// </summary>
    void Present();
}
