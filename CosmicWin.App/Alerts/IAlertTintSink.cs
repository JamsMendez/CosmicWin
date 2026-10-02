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
    /// <summary>
    /// Raised (any thread) when the requested tint first actually reached the screen; the page must not
    /// stop painting its letters before this. Raised again after a <see cref="TintLost"/> recovery.
    /// </summary>
    event Action? TintRendered;

    /// <summary>Raised (any thread) when a tint that was rendering stopped (failure/back-off): letters must be painted again.</summary>
    event Action? TintLost;

    void SetTint(ReadOnlyMemory<byte> maskAlpha, int width, int height, byte r, byte g, byte b);

    void ClearTint();
}
