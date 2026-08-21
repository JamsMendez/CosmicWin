using CosmicWin.App.Tray;

namespace CosmicWin.App.Tests.Tray;

/// <summary>
/// Tasks 3.15/3.17/3.36 (WU11): <see cref="TrayMenuController"/> is a pure, delegate-based
/// pass-through -- no hidden state, no Win32, no live tray needed to test menu behavior. Mirrors
/// <see cref="WorkspaceSessionAdapter"/>'s injected-<c>Func</c> single-source-of-truth idiom.
/// </summary>
public sealed class TrayMenuControllerTests
{
    [Fact]
    public void TogglePause_FlipsFromFalseToTrue_AndReturnsNewState()
    {
        var paused = false;
        var controller = new TrayMenuController(() => paused, value => paused = value, () => { }, () => { });

        var result = controller.TogglePause();

        Assert.True(result);
        Assert.True(paused);
    }

    [Fact]
    public void TogglePause_CalledTwice_ReturnsToOriginalState()
    {
        var paused = false;
        var controller = new TrayMenuController(() => paused, value => paused = value, () => { }, () => { });

        controller.TogglePause();
        var result = controller.TogglePause();

        Assert.False(result);
        Assert.False(paused);
    }

    /// <summary>Proves the controller has no hidden state of its own -- it always reflects the injected getter, even when that state changes externally (not through the controller).</summary>
    [Fact]
    public void IsPaused_ReflectsInjectedGetter_NotInternalState()
    {
        var paused = false;
        var controller = new TrayMenuController(() => paused, value => paused = value, () => { }, () => { });
        Assert.False(controller.IsPaused);

        paused = true;

        Assert.True(controller.IsPaused);
    }

    [Fact]
    public void Reload_InvokesInjectedReloadDelegate_ExactlyOnce()
    {
        var reloadCount = 0;
        var controller = new TrayMenuController(() => false, _ => { }, () => reloadCount++, () => { });

        controller.Reload();

        Assert.Equal(1, reloadCount);
    }

    [Fact]
    public void Exit_InvokesInjectedExitDelegate_ExactlyOnce()
    {
        var exitCount = 0;
        var controller = new TrayMenuController(() => false, _ => { }, () => { }, () => exitCount++);

        controller.Exit();

        Assert.Equal(1, exitCount);
    }
}
