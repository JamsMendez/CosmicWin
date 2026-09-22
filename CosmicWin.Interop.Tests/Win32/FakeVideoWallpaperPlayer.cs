using CosmicWin.Interop;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// In-memory <see cref="IVideoWallpaperPlayer"/>, for App-layer composition tests (e.g. T6's
/// tray-menu/AppComposition wiring) that need to construct something depending on this interface
/// without touching real Media Foundation or a real GPU. Mirrors <see cref="FakeVideoWallpaperHost"/>'s
/// call-recording style.
/// </summary>
internal sealed class FakeVideoWallpaperPlayer : IVideoWallpaperPlayer
{
    public int TryPlayCallCount { get; private set; }

    /// <summary>What the next (and every subsequent) <see cref="TryPlay"/> call returns.</summary>
    public bool TryPlayReturns { get; set; } = true;

    public IVideoWallpaperHost? LastHost { get; private set; }

    public string? LastVideoPath { get; private set; }

    public int DisposeCallCount { get; private set; }

    public int StopCallCount { get; private set; }

    public bool TryPlay(IVideoWallpaperHost host, string videoPath)
    {
        TryPlayCallCount++;
        LastHost = host;
        LastVideoPath = videoPath;
        return TryPlayReturns;
    }

    public void Stop() => StopCallCount++;

    public void Dispose() => DisposeCallCount++;
}
