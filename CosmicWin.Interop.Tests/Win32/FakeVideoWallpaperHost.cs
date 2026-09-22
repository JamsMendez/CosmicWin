using CosmicWin.Interop.Win32;
using Windows.Win32.Graphics.Direct3D11;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// In-memory <see cref="IVideoWallpaperHost"/>, for App-layer composition tests (a later task,
/// e.g. the Media Foundation frame-server wrapper and the tray-menu wiring) that need to construct
/// something depending on this interface without touching a real desktop or a real GPU. Mirrors
/// <see cref="FakeNativeDisplaySource"/>'s shape: records calls rather than simulating real D3D
/// state, since nothing before the real Media Foundation task needs the fake to hand back a usable
/// device or texture.
/// </summary>
internal sealed class FakeVideoWallpaperHost : IVideoWallpaperHost
{
    public int TryAttachCallCount { get; private set; }

    /// <summary>What the next (and every subsequent) <see cref="TryAttach"/> call returns.</summary>
    public bool TryAttachReturns { get; set; } = true;

    public int PresentCallCount { get; private set; }

    public int DisposeCallCount { get; private set; }

    public bool TryAttach()
    {
        TryAttachCallCount++;
        return TryAttachReturns;
    }

    public ID3D11Device Device =>
        throw new InvalidOperationException(
            "FakeVideoWallpaperHost never creates a real D3D11 device -- a consumer under test " +
            "should not read Device through this fake.");

    public ID3D11Texture2D GetBackBuffer() =>
        throw new InvalidOperationException(
            "FakeVideoWallpaperHost never creates a real back buffer -- a consumer under test " +
            "should not call GetBackBuffer through this fake.");

    public void Present() => PresentCallCount++;

    public void Dispose() => DisposeCallCount++;
}
