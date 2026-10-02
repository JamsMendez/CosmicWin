namespace CosmicWin.App.Alerts;

/// <summary>
/// see-through-video-tint (S4): where the alert layer sends the letters mask so the REAL video behind
/// the letters can be tinted through it. Only exists in video wallpaper mode (production wraps
/// <c>MediaFoundationVideoWallpaperPlayer.SetTint</c>/<c>ClearTint</c>); html / mini modes pass none.
/// </summary>
/// <remarks>
/// Contract (same as the player): <paramref name="maskAlpha"/> is an 8-bit alpha mask, row-major,
/// tightly packed, top row first (0 = untouched, 255 = fully tinted); the implementation copies it
/// during the call. Callable from any thread.
/// </remarks>
public interface IAlertTintSink
{
    void SetTint(ReadOnlyMemory<byte> maskAlpha, int width, int height, byte r, byte g, byte b);

    void ClearTint();
}
