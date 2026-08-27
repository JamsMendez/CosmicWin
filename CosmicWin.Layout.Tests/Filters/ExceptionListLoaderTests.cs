using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.Layout.Tests.Filters;

/// <summary>
/// WE-2 plain-text config file format: <c>kind:value</c> lines, comments, and blank lines.
/// </summary>
public class ExceptionListLoaderTests
{
    private static WindowDescriptor SpotifyWindow() => new(
        ClassName: "Chrome_WidgetWin_1",
        ProcessName: "Spotify.exe",
        Title: "Spotify Premium",
        ExStyle: 0,
        Style: WindowStyleFlags.SystemMenu | WindowStyleFlags.MaximizeBox | WindowStyleFlags.MinimizeBox,
        IsOwned: false,
        Width: 800,
        Height: 600);

    [Fact]
    public void Parse_ProcessLine_ProducesMatchingExceptionList()
    {
        var exceptions = ExceptionListLoader.Parse("process:Spotify.exe");

        Assert.True(exceptions.Matches(SpotifyWindow()));
    }

    [Fact]
    public void Parse_MultipleLinesWithCommentsAndBlankLines_IgnoresNonRuleLines()
    {
        // Triangulation: comments, blank lines, and multiple rule kinds in one file.
        var content = "# user exceptions\n\nprocess:Spotify.exe\ntitle:Preferences\nclass:ToolTip\n";

        var exceptions = ExceptionListLoader.Parse(content);

        Assert.True(exceptions.Matches(SpotifyWindow()));
        Assert.True(exceptions.Matches(SpotifyWindow() with { ProcessName = "other.exe", Title = "Preferences window" }));
        Assert.True(exceptions.Matches(SpotifyWindow() with { ProcessName = "other.exe", ClassName = "ToolTip" }));
    }

    [Fact]
    public void Parse_MalformedOrUnrecognizedLines_AreSkipped_NotThrown()
    {
        var content = "not-a-rule-line\nbogus:value\nprocess:\n:missingkind\nprocess:Spotify.exe";

        var exceptions = ExceptionListLoader.Parse(content);

        // Only the trailing well-formed line should have produced a rule.
        Assert.True(exceptions.Matches(SpotifyWindow()));
        Assert.False(exceptions.Matches(SpotifyWindow() with { ProcessName = "notepad.exe", Title = "x", ClassName = "y" }));
    }

    [Fact]
    public void Parse_EmptyContent_ProducesEmptyExceptionList()
    {
        var exceptions = ExceptionListLoader.Parse(string.Empty);

        Assert.False(exceptions.Matches(SpotifyWindow()));
    }
}
