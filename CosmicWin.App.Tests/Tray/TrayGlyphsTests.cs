using CosmicWin.App.Tray;

namespace CosmicWin.App.Tests.Tray;

/// <summary>
/// Which glyph each tray command wears.
/// </summary>
/// <remarks>
/// <para>
/// The choice is pure and pinned here; the DRAWING of it needs a live desktop, a font and a DPI, and
/// is verified by hand like the rest of <see cref="TrayIconHost"/>.
/// </para>
/// <para>
/// .NET ships no icon set that covers these -- <c>SystemIcons</c> stops at Error, Warning, Shield
/// and friends. Windows itself does, as a FONT: <c>Segoe Fluent Icons</c> on 11, <c>Segoe MDL2
/// Assets</c> on 10. A font is the right shape for this: it is already installed, it is what the
/// shell's own menus use, and it renders at whatever size and DPI the notification area asks for
/// instead of smearing one bitmap to fit.
/// </para>
/// </remarks>
public sealed class TrayGlyphsTests
{
    /// <summary>
    /// The icon says what the command DOES, never what the state currently is -- the same rule the
    /// label already follows. Paused offers Play, because the offer is to resume.
    /// </summary>
    [Theory]
    [InlineData(false, TrayGlyphs.Pause)]
    [InlineData(true, TrayGlyphs.Play)]
    public void PauseGlyph_OffersTheActionNotTheState(bool isPaused, string expected)
    {
        Assert.Equal(expected, TrayGlyphs.ForPause(isPaused));
    }

    /// <summary>
    /// Glyph and label must flip together. They are two renderings of one decision, and a menu
    /// reading "Reanudar" beside a pause icon is worse than no icon at all.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PauseGlyphAndPauseLabel_AgreeOnEveryState(bool isPaused)
    {
        var offersResume = TrayIconHost.PauseLabel(isPaused) == "Reanudar";
        var offersPlay = TrayGlyphs.ForPause(isPaused) == TrayGlyphs.Play;

        Assert.Equal(offersResume, offersPlay);
    }

    /// <summary>Every glyph is a single code point: one character, one icon, no ligature to resolve.</summary>
    [Theory]
    [InlineData(TrayGlyphs.Pause)]
    [InlineData(TrayGlyphs.Play)]
    [InlineData(TrayGlyphs.Refresh)]
    [InlineData(TrayGlyphs.Exit)]
    public void EveryGlyphIsExactlyOneCharacter(string glyph)
    {
        Assert.Equal(1, glyph.Length);
    }

    /// <summary>
    /// All four live in the Private Use Area, which is where both Segoe icon fonts put their
    /// glyphs. A code point outside it would render as a letter in a menu -- silently, and only on
    /// the machines missing the font.
    /// </summary>
    [Theory]
    [InlineData(TrayGlyphs.Pause)]
    [InlineData(TrayGlyphs.Play)]
    [InlineData(TrayGlyphs.Refresh)]
    [InlineData(TrayGlyphs.Exit)]
    public void EveryGlyphSitsInThePrivateUseArea(string glyph)
    {
        Assert.InRange(glyph[0], '\uE000', '\uF8FF');
    }
}
