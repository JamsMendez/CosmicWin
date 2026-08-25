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
}
