using CosmicWin.Interop.Win32;

namespace CosmicWin.App.Alerts;

/// <summary>
/// see-through-video-tint (S4): adapts the concrete video player's SetTint/ClearTint to <see
/// cref="IAlertTintSink"/>, and decides WHEN a sink exists at all: only in video wallpaper mode.
/// </summary>
internal sealed class VideoPlayerAlertTintSink : IAlertTintSink
{
    private readonly MediaFoundationVideoWallpaperPlayer _player;

    private VideoPlayerAlertTintSink(MediaFoundationVideoWallpaperPlayer player) => _player = player;

    /// <summary>The sink for <paramref name="mode"/>: <see langword="null"/> unless it is video mode with a player.</summary>
    public static IAlertTintSink? For(WallpaperMode mode, MediaFoundationVideoWallpaperPlayer? player) =>
        mode == WallpaperMode.Video && player is not null ? new VideoPlayerAlertTintSink(player) : null;

    public void SetTint(ReadOnlyMemory<byte> maskAlpha, int width, int height, byte r, byte g, byte b) =>
        _player.SetTint(maskAlpha, width, height, r, g, b);

    public void ClearTint() => _player.ClearTint();
}
