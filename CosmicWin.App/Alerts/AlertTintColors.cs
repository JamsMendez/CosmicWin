namespace CosmicWin.App.Alerts;

/// <summary>
/// see-through-video-tint (S4): the full-brightness tint of the real video inside the alert letters
/// (white video pixels become exactly this color, darker ones a darker shade). Named constants so the
/// look is easy to tweak in one place.
/// </summary>
internal static class AlertTintColors
{
    /// <summary>Failed alerts: a saturated blue, rgb(40,110,255).</summary>
    public static readonly (byte R, byte G, byte B) Failed = (40, 110, 255);

    /// <summary>Warning alerts: violet, rgb(150,70,255).</summary>
    public static readonly (byte R, byte G, byte B) Warning = (150, 70, 255);
}
