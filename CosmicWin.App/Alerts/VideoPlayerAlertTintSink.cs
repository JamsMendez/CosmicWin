using CosmicWin.Interop.Win32;

namespace CosmicWin.App.Alerts;

/// <summary>
/// see-through-video-tint (S4): adapts the concrete video player's SetTint/ClearTint to <see
/// cref="IAlertTintSink"/>, and decides WHEN a sink exists at all: only when a video player exists.
/// </summary>
internal sealed class VideoPlayerAlertTintSink : IAlertTintSink
{
    private readonly MediaFoundationVideoWallpaperPlayer _player;

    private VideoPlayerAlertTintSink(MediaFoundationVideoWallpaperPlayer player) => _player = player;

    /// <summary>The sink for <paramref name="player"/>: <see langword="null"/> when there is no player.</summary>
    public static IAlertTintSink? For(MediaFoundationVideoWallpaperPlayer? player) =>
        player is not null ? new VideoPlayerAlertTintSink(player) : null;

    public event Action? TintRendered
    {
        add => _player.TintRendered += value;
        remove => _player.TintRendered -= value;
    }

    public event Action? TintLost
    {
        add => _player.TintLost += value;
        remove => _player.TintLost -= value;
    }

    public void SetTint(ReadOnlyMemory<byte> maskAlpha, int width, int height, byte r, byte g, byte b) =>
        _player.SetTint(maskAlpha, width, height, r, g, b);

    public void ClearTint() => _player.ClearTint();
}
