namespace CosmicWin.Interop.Win32;

/// <summary>
/// Abstracts looping video playback into an already-attached <see cref="IVideoWallpaperHost"/>.
/// </summary>
/// <remarks>
/// <see cref="MediaFoundationVideoWallpaperPlayer"/> is the real, CsWin32-backed implementation,
/// wrapping <c>IMFMediaEngine</c> in frame-server mode (own D3D11 device manager, no playback
/// HWND) so it pulls decoded frames into the host's own swapchain via <c>TransferVideoFrame</c>
/// rather than rendering into a window itself -- see that class's remarks and
/// <c>odd/tasks/video-wallpaper.md</c> for why legacy HWND mode is out of scope. Tests substitute
/// an in-memory <c>FakeVideoWallpaperPlayer</c>.
/// </remarks>
internal interface IVideoWallpaperPlayer : IDisposable
{
    /// <summary>
    /// Starts (or restarts, if already playing) looping, muted playback of the file at
    /// <paramref name="videoPath"/> into <paramref name="host"/>, which must already have had
    /// <see cref="IVideoWallpaperHost.TryAttach"/> succeed at least once. Never throws; returns
    /// <see langword="false"/> on failure (missing file, engine creation failure, ...).
    /// </summary>
    bool TryPlay(IVideoWallpaperHost host, string videoPath);
}
