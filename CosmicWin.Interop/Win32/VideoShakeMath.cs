namespace CosmicWin.Interop.Win32;

/// <summary>
/// The 2D transform to apply to the video for one instant of the native `failed` shake (T4,
/// webview-alert-layer) -- (<see cref="Dx"/>, <see cref="Dy"/>) a translation in pixels,
/// <see cref="AngleDegrees"/> a rotation, <see cref="Scale"/> a uniform scale about the video's own
/// centre.
/// </summary>
public readonly record struct VideoShakeTransform(double Dx, double Dy, double AngleDegrees, double Scale)
{
    /// <summary>No effect: the video presents exactly as it always does.</summary>
    public static readonly VideoShakeTransform Identity = new(0, 0, 0, 1.0);
}

/// <summary>
/// Pure port of the alert page's own <c>applyFailureShake</c> (see
/// <c>backgroud-processing/script.js</c>, and the T1 page's remarks for why the CANVAS shake was
/// dropped there in favour of this native one) -- a function of elapsed time and the back buffer's
/// own size, with no DirectComposition, host, or player state anywhere near it. Kept a separate,
/// fully pure class (rather than inlined into <see cref="MediaFoundationVideoWallpaperPlayer"/>)
/// for exactly that reason: this is the whole of what a unit test needs to hold the formulas to
/// the page's own numbers.
/// </summary>
public static class VideoShakeMath
{
    /// <summary>
    /// How long the shake decays over, in milliseconds -- matches the page's own
    /// <c>FAILURE_SHAKE_MS</c>. The legacy Direct2D overlay has independent timing.
    /// Past this many milliseconds elapsed,
    /// <see cref="Compute"/> always returns <see cref="VideoShakeTransform.Identity"/>.
    /// </summary>
    public const double DurationMilliseconds = 120;

    /// <summary>
    /// The transform for one instant <paramref name="elapsedMilliseconds"/> into the shake, over a
    /// video whose current back buffer is <paramref name="width"/> by <paramref name="height"/>
    /// pixels. Identity before the shake starts (a negative elapsed) and from
    /// <see cref="DurationMilliseconds"/> onward -- a caller keeps calling this every tick and stops
    /// treating the shake as active once it sees the identity transform for good.
    /// </summary>
    public static VideoShakeTransform Compute(double elapsedMilliseconds, double width, double height)
    {
        if (elapsedMilliseconds < 0 || elapsedMilliseconds >= DurationMilliseconds)
        {
            return VideoShakeTransform.Identity;
        }

        double decay = 1 - elapsedMilliseconds / DurationMilliseconds;
        double amplitude = Math.Min(width, height) * (0.5 + 0.5 * decay);
        double t = elapsedMilliseconds / 1000.0;

        double dx = amplitude * 0.1 * (Math.Sin(t * 131) * 0.65 + Math.Sin(t * 211 + 1.3) * 0.35);
        double dy = amplitude * 0.04 * (Math.Sin(t * 109 + 2.1) * 0.6 + Math.Sin(t * 197 + 0.4) * 0.4);
        double angle = 3.5 * decay * Math.Sin(t * 89 + 0.7);

        return new VideoShakeTransform(dx, dy, angle, 1.18);
    }
}
