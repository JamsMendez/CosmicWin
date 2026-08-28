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
    /// The order the maintainer asked for: the border first, then the pause, then the two items
    /// that end something.
    /// </summary>
    /// <remarks>
    /// This is the REAL order, not a copy of it -- the constructor adds its items by walking this
    /// same list, so a menu built in a different order cannot pass. A test that restated the order
    /// in its own literal would assert nothing but its own copy.
    /// </remarks>
    [Fact]
    public void TheMenuIsOrdered_BorderThenPauseThenReloadThenExit()
    {
        Assert.Equal(
            [
                TrayMenuEntry.FocusBorder,
                TrayMenuEntry.BorderColor,
                TrayMenuEntry.Pause,
                TrayMenuEntry.Reload,
                TrayMenuEntry.Exit,
            ],
            TrayIconHost.MenuOrder);
    }

    /// <summary>Every entry the menu knows about is placed. A new one must not be silently dropped.</summary>
    [Fact]
    public void EveryEntryAppearsExactlyOnce()
    {
        Assert.Equal(Enum.GetValues<TrayMenuEntry>().Order(), TrayIconHost.MenuOrder.Order());
    }
}
