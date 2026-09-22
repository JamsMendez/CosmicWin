using CosmicWin.Interop.Win32;

namespace CosmicWin.Interop;

/// <summary>
/// Abstracts looping video playback into an already-attached <see cref="IVideoWallpaperHost"/>.
/// </summary>
/// <remarks>
/// <see cref="MediaFoundationVideoWallpaperPlayer"/> is the real, CsWin32-backed implementation,
/// wrapping <c>IMFMediaEngine</c> in frame-server mode (own D3D11 device manager, no playback
/// HWND) so it pulls decoded frames into the host's own swapchain via <c>TransferVideoFrame</c>
/// rather than rendering into a window itself -- see that class's remarks and
/// <c>odd/tasks/video-wallpaper.md</c> for why legacy HWND mode is out of scope. Tests substitute
/// an in-memory <c>FakeVideoWallpaperPlayer</c>. Public (root <c>CosmicWin.Interop</c> namespace),
/// same reason as <see cref="IVideoWallpaperHost"/>: <c>CosmicWin.App</c>'s <c>AppComposition</c>
/// (T6) constructs the real implementation directly and is not covered by this project's
/// <c>InternalsVisibleTo</c>.
/// </remarks>
public interface IVideoWallpaperPlayer : IDisposable
{
    /// <summary>
    /// Starts (or restarts, if already playing) looping, muted playback of the file at
    /// <paramref name="videoPath"/> into <paramref name="host"/>, which must already have had
    /// <see cref="IVideoWallpaperHost.TryAttach"/> succeed at least once. Never throws; returns
    /// <see langword="false"/> on failure (missing file, engine creation failure, ...).
    /// </summary>
    bool TryPlay(IVideoWallpaperHost host, string videoPath);

    /// <summary>
    /// Stops any playback currently in progress and releases whatever file is behind it, so a
    /// caller that is about to overwrite that file on disk (see <c>VideoWallpaperImport.Import</c>'s
    /// fixed destination) can safely do so once this returns.
    /// </summary>
    /// <remarks>
    /// Idempotent and safe to call when nothing is playing, including before the first ever
    /// <see cref="TryPlay"/> call and after <see cref="IDisposable.Dispose"/>. Never throws.
    /// <see cref="TryPlay"/> remains fully usable afterwards -- <c>Stop</c> tears playback down
    /// without retiring the player itself, unlike <see cref="IDisposable.Dispose"/>.
    /// </remarks>
    void Stop();
}
