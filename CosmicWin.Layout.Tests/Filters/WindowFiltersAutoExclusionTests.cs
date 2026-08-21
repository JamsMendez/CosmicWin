using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.Layout.Tests.Filters;

/// <summary>
/// WE-1: automatic exclusion heuristics — <c>WS_EX_TOOLWINDOW</c>, owned dialogs lacking both
/// maximize and minimize boxes, and windows lacking <c>WS_SYSMENU</c>. Covers spec scenario
/// "Splash screen excluded automatically" and the two other WE-1 heuristic clauses.
/// </summary>
public class WindowFiltersAutoExclusionTests
{
    private static WindowDescriptor Normal() => new(
        ClassName: "Notepad",
        ProcessName: "notepad.exe",
        Title: "Untitled - Notepad",
        ExStyle: 0,
        Style: WindowStyleFlags.SystemMenu | WindowStyleFlags.MaximizeBox | WindowStyleFlags.MinimizeBox,
        IsOwned: false);

    [Fact]
    public void IsAutoExcluded_NormalTopLevelWindow_ReturnsFalse()
    {
        Assert.False(WindowFilters.IsAutoExcluded(Normal()));
    }

    [Fact]
    public void IsAutoExcluded_ToolWindowExStyle_ReturnsTrue()
    {
        // Spec scenario: "Splash screen excluded automatically" — WS_EX_TOOLWINDOW set.
        var descriptor = Normal() with { ExStyle = WindowStyleFlags.ExToolWindow };

        Assert.True(WindowFilters.IsAutoExcluded(descriptor));
    }

    [Fact]
    public void IsAutoExcluded_MissingSystemMenu_ReturnsTrue()
    {
        var descriptor = Normal() with { Style = WindowStyleFlags.MaximizeBox | WindowStyleFlags.MinimizeBox };

        Assert.True(WindowFilters.IsAutoExcluded(descriptor));
    }

    [Fact]
    public void IsAutoExcluded_OwnedWindowLackingBothMaximizeAndMinimizeBox_ReturnsTrue()
    {
        var descriptor = Normal() with { Style = WindowStyleFlags.SystemMenu, IsOwned = true };

        Assert.True(WindowFilters.IsAutoExcluded(descriptor));
    }

    [Fact]
    public void IsAutoExcluded_OwnedWindowWithMaximizeBox_ReturnsFalse()
    {
        // Triangulation: "lacking WS_MAXIMIZEBOX/WS_MINIMIZEBOX" requires lacking BOTH — an owned
        // window that still has a maximize box is not the dialog shape WE-1 targets.
        var descriptor = Normal() with
        {
            Style = WindowStyleFlags.SystemMenu | WindowStyleFlags.MaximizeBox,
            IsOwned = true,
        };

        Assert.False(WindowFilters.IsAutoExcluded(descriptor));
    }
}
