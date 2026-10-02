using CosmicWin.App.Tray;

namespace CosmicWin.App.Tests.Tray;

/// <summary>
/// <see cref="TrayIconHost.PauseLabel"/> is the one sliver of <see
/// cref="TrayIconHost"/> logic extracted as a pure function and unit-tested -- everything else in
/// that class needs a live Win32 desktop/taskbar and is covered only by the manual verification
/// checklist recorded in apply-progress.
/// </summary>
public sealed class TrayIconHostTests
{
    [Theory]
    [InlineData(false, "Pausar")]
    [InlineData(true, "Reanudar")]
    public void PauseLabel_ReflectsPausedState(bool isPaused, string expected)
    {
        Assert.Equal(expected, TrayIconHost.PauseLabel(isPaused));
    }

    /// <summary>
    /// The mode switch first, then the border and its colour, then the video wallpaper picker and
    /// its remove item right after it, then the pause, then the two items
    /// that end something.
    /// </summary>
    /// <remarks>
    /// This is the REAL order, not a copy of it -- the constructor adds its items by walking this
    /// same list, so a menu built in a different order cannot pass. A test that restated the order
    /// in its own literal would assert nothing but its own copy.
    /// </remarks>
    [Fact]
    public void TheMenuIsOrdered_TilingThenBorderThenVideoWallpaperThenRemoveVideoThenPauseThenReloadThenExit()
    {
        Assert.Equal(
            [
                TrayMenuEntry.Tiling,
                TrayMenuEntry.FocusBorder,
                TrayMenuEntry.BorderColor,
                TrayMenuEntry.VideoWallpaper,
                TrayMenuEntry.RemoveVideoWallpaper,
                TrayMenuEntry.Pause,
                TrayMenuEntry.Reload,
                TrayMenuEntry.Exit,
            ],
            TrayIconHost.MenuOrder);
    }

    /// <summary>
    /// "Quitar wallpaper de video" is shown only while a video wallpaper is loaded and HIDDEN (not
    /// greyed) otherwise. The same refresh runs at construction and on every menu Opening, so the
    /// item appears after a pick and disappears after a removal.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RefreshRemoveVideoWallpaperItem_ShowsTheItemOnlyWhileAVideoIsLoaded(bool loaded)
    {
        var controller = new TrayMenuController(
            () => false, _ => { }, () => true, _ => { }, () => { }, () => { },
            getHasVideoWallpaper: () => loaded);
        using var item = new System.Windows.Forms.ToolStripMenuItem { Available = !loaded };

        TrayIconHost.RefreshRemoveVideoWallpaperItem(item, controller);

        Assert.Equal(loaded, item.Available);
        Assert.True(item.Enabled);
    }

    /// <summary>Every entry the menu knows about is placed. A new one must not be silently dropped.</summary>
    [Fact]
    public void EveryEntryAppearsExactlyOnce()
    {
        Assert.Equal(Enum.GetValues<TrayMenuEntry>().Order(), TrayIconHost.MenuOrder.Order());
    }
}
