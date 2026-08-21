using CosmicWin.App.Tray;

namespace CosmicWin.App.Tests.Tray;

/// <summary>
/// Task 3.16 (WU11): <see cref="TrayIconHost.PauseLabel"/> is the one sliver of <see
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
}
