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

    [Fact]
    public void IsAutoExcluded_UnownedWindowLackingBothMaximizeAndMinimizeBox_ReturnsFalse()
    {
        // Pins WE-1's "owned windows lacking WS_MAXIMIZEBOX/WS_MINIMIZEBOX (dialogs)" restriction
        // (verify-report #21 rev 9 V9-W2): an UNOWNED fixed-size top-level window that merely
        // cannot be maximized or minimized is a legitimate window, not the dialog shape WE-1
        // targets, and must NOT be auto-excluded — only OWNED windows lacking both boxes qualify.
        var descriptor = Normal() with { Style = WindowStyleFlags.SystemMenu, IsOwned = false };

        Assert.False(WindowFilters.IsAutoExcluded(descriptor));
    }

    /// <summary>
    /// Measured on real hardware 2026-08-22. The maintainer launched CosmicWin with a minimized
    /// Brave window on the desktop and the Windows Terminal took only HALF the screen: the filter
    /// chain admitted the minimized window, so the tree held two leaves and handed an entire tile
    /// to something that is not drawn anywhere. The diagnostic snapshot recorded it verbatim --
    /// <c>ADMIT 0xC0856 proc=brave.exe rect=[L=-32000 T=-32000 W=160 H=28]</c>, which is Win32's
    /// canonical parking spot for a minimized window.
    /// <para>
    /// Keyed off <c>WS_MINIMIZE</c> rather than those coordinates: the style bit is the documented
    /// signal, while (-32000,-32000) is an implementation detail that also shows up on windows
    /// deliberately parked off-screen.
    /// </para>
    /// </summary>
    [Fact]
    public void IsAutoExcluded_MinimizedWindow_ReturnsTrue()
    {
        var descriptor = Normal() with { Style = Normal().Style | WindowStyleFlags.Minimized };

        Assert.True(WindowFilters.IsAutoExcluded(descriptor));
    }
}
