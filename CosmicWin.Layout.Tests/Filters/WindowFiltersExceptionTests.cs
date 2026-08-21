using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.Layout.Tests.Filters;

/// <summary>
/// WE-2: manual exception list applied in addition to WE-1. Covers spec scenario "Manual
/// exception overrides heuristic tiling".
/// </summary>
public class WindowFiltersExceptionTests
{
    private static WindowDescriptor TileableSpotifyWindow() => new(
        ClassName: "Chrome_WidgetWin_1",
        ProcessName: "Spotify.exe",
        Title: "Spotify Premium",
        ExStyle: 0,
        Style: WindowStyleFlags.SystemMenu | WindowStyleFlags.MaximizeBox | WindowStyleFlags.MinimizeBox,
        IsOwned: false);

    [Fact]
    public void IsExcluded_NormalWindow_NotAutoExcluded_NoExceptionMatch_ReturnsFalse()
    {
        var descriptor = TileableSpotifyWindow();
        var exceptions = new ExceptionList([]);

        Assert.False(WindowFilters.IsExcluded(descriptor, exceptions));
    }

    [Fact]
    public void IsExcluded_ProcessNameInExceptionList_ReturnsTrue_EvenWhenOtherwiseTileable()
    {
        // Spec scenario: "Manual exception overrides heuristic tiling" — Spotify.exe in the
        // exception list excludes an otherwise-tileable normal top-level window.
        var descriptor = TileableSpotifyWindow();
        var exceptions = new ExceptionList([new ExceptionRule(ExceptionRuleKind.ProcessName, "Spotify.exe")]);

        Assert.True(WindowFilters.IsExcluded(descriptor, exceptions));
    }

    [Fact]
    public void IsExcluded_TitleSubstringMatch_ReturnsTrue()
    {
        var descriptor = TileableSpotifyWindow() with { Title = "Settings — Preferences" };
        var exceptions = new ExceptionList([new ExceptionRule(ExceptionRuleKind.TitleSubstring, "Preferences")]);

        Assert.True(WindowFilters.IsExcluded(descriptor, exceptions));
    }

    [Fact]
    public void IsExcluded_ClassNameMatch_ReturnsTrue()
    {
        var descriptor = TileableSpotifyWindow();
        var exceptions = new ExceptionList([new ExceptionRule(ExceptionRuleKind.ClassName, "Chrome_WidgetWin_1")]);

        Assert.True(WindowFilters.IsExcluded(descriptor, exceptions));
    }

    [Fact]
    public void IsExcluded_NonMatchingExceptionEntries_ReturnsFalse()
    {
        // Triangulation: a non-empty exception list that simply doesn't match this window must
        // not force exclusion — proves Matches is a real per-rule comparison, not a truthy list check.
        var descriptor = TileableSpotifyWindow();
        var exceptions = new ExceptionList([new ExceptionRule(ExceptionRuleKind.ProcessName, "notepad.exe")]);

        Assert.False(WindowFilters.IsExcluded(descriptor, exceptions));
    }
}
