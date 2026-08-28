namespace CosmicWin.App.Tests;

/// <summary>
/// The settings file's text format, tested as the pure function it is.
/// </summary>
/// <remarks>
/// Separate from <see cref="SettingsFileTests"/> on purpose, and for the same reason
/// <see cref="CosmicWin.Layout.Filters.ExceptionListLoader"/> takes text rather than a path: the
/// FORMAT is where a typo silently changes behaviour, and it deserves facts that never touch a disk.
/// </remarks>
public sealed class SettingsTests
{
    /// <summary>
    /// The border is ON unless the file says otherwise. A settings file that has never been written
    /// must not turn a feature off.
    /// </summary>
    [Fact]
    public void EmptyContent_LeavesEveryDefaultInPlace()
    {
        var settings = Settings.Parse(string.Empty);

        Assert.True(settings.FocusBorder);
        Assert.Equal(Settings.Default, settings);
    }

    [Theory]
    [InlineData("focus-border = off")]
    [InlineData("focus-border=off")]
    [InlineData("  focus-border   =   off  ")]
    [InlineData("FOCUS-BORDER = OFF")]
    [InlineData("focus-border = false")]
    [InlineData("focus-border = 0")]
    public void TheBorderIsTurnedOff_HoweverTheLineIsSpelled(string line)
    {
        Assert.False(Settings.Parse(line).FocusBorder);
    }

    [Theory]
    [InlineData("focus-border = on")]
    [InlineData("focus-border = true")]
    [InlineData("focus-border = 1")]
    public void TheBorderIsTurnedOn_HoweverTheLineIsSpelled(string line)
    {
        Assert.True(Settings.Parse(line).FocusBorder);
    }

    /// <summary>
    /// A value nobody recognises is not a reason to change what the user is looking at. It keeps the
    /// default, exactly as an unparseable exception-list line is skipped rather than thrown on.
    /// </summary>
    [Theory]
    [InlineData("focus-border = perhaps")]
    [InlineData("focus-border =")]
    [InlineData("focus-border")]
    public void AnUnreadableValue_KeepsTheDefaultRatherThanGuessing(string line)
    {
        Assert.True(Settings.Parse(line).FocusBorder);
    }

    [Fact]
    public void CommentsBlankLinesAndUnknownKeys_AreIgnored()
    {
        var settings = Settings.Parse(
            """
            # CosmicWin settings

            gap = 12
            focus-border = off
            """);

        Assert.False(settings.FocusBorder);
    }

    /// <summary>The last word wins, so a file appended to twice reads as its most recent line.</summary>
    [Fact]
    public void TheLastAssignmentWins()
    {
        Assert.True(Settings.Parse("focus-border = off\nfocus-border = on").FocusBorder);
    }

    [Fact]
    public void CarriageReturnsAreNotPartOfTheValue()
    {
        Assert.False(Settings.Parse("focus-border = off\r\n").FocusBorder);
    }

    /// <summary>
    /// What is written must read back as itself. This is the fact that catches a serializer and a
    /// parser drifting apart -- the failure mode where saving a toggle silently resets it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SerializeThenParse_RoundTrips(bool focusBorder)
    {
        var original = new Settings(focusBorder);

        Assert.Equal(original, Settings.Parse(original.Serialize()));
    }

    /// <summary>The written file explains itself, because a human is expected to open it.</summary>
    [Fact]
    public void TheSerializedFileCarriesItsOwnComment()
    {
        Assert.Contains("#", new Settings(false).Serialize(), StringComparison.Ordinal);
    }

    /// <summary>
    /// No colour is a real answer, not a missing one: it means "whatever Windows' accent is right
    /// now", which is what the border drew before it could be configured at all.
    /// </summary>
    [Fact]
    public void NoBorderColour_MeansTheSystemAccent()
    {
        Assert.Null(Settings.Default.BorderColor);
        Assert.Null(Settings.Parse(string.Empty).BorderColor);
    }

    [Theory]
    [InlineData("border-color = #FF8800")]
    [InlineData("border-color=#ff8800")]
    [InlineData("  BORDER-COLOR  =  #Ff8800  ")]
    [InlineData("border-color = FF8800")]
    public void AColourIsRead_HoweverTheLineIsSpelled(string line)
    {
        Assert.Equal(0xFF8800u, Settings.Parse(line).BorderColor);
    }

    /// <summary>
    /// The three-digit form is what a person types from memory, and CSS taught them it works.
    /// Each digit doubles, so <c>#f80</c> is the same colour as <c>#ff8800</c> rather than a
    /// near-miss nobody can see is wrong.
    /// </summary>
    [Fact]
    public void TheShortHexForm_ExpandsTheWayCssDoes()
    {
        Assert.Equal(0xFF8800u, Settings.Parse("border-color = #f80").BorderColor);
    }

    /// <summary>The word that says "give it back to Windows", so the tray has a way home.</summary>
    [Fact]
    public void TheWordAccent_ClearsTheColour()
    {
        Assert.Null(Settings.Parse("border-color = #FF8800\nborder-color = accent").BorderColor);
    }

    /// <summary>
    /// Same rule the flag follows: only a value we RECOGNISE moves the setting. Guessing what
    /// "blue-ish" meant is worse than leaving the accent alone.
    /// </summary>
    [Theory]
    [InlineData("border-color = azul")]
    [InlineData("border-color = #12345")]
    [InlineData("border-color = #GGGGGG")]
    [InlineData("border-color =")]
    public void AnUnreadableColour_LeavesTheDefaultAlone(string line)
    {
        Assert.Null(Settings.Parse(line).BorderColor);
    }

    /// <summary>A bad colour costs the colour, never the flag that shares the file.</summary>
    [Fact]
    public void AnUnreadableColour_DoesNotCostTheOtherSetting()
    {
        Assert.False(Settings.Parse("focus-border = off\nborder-color = azul").FocusBorder);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0x000000u)]
    [InlineData(0xFF8800u)]
    [InlineData(0xFFFFFFu)]
    public void SerializeThenParse_RoundTripsTheColour(uint? colour)
    {
        var original = new Settings(FocusBorder: true, BorderColor: colour);

        Assert.Equal(original, Settings.Parse(original.Serialize()));
    }
}
