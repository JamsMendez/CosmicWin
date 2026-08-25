using CosmicWin.App.Tray;

namespace CosmicWin.App.Tests.Tray;

/// <summary>
/// <see cref="TrayMenuController"/> is a pure, delegate-based
/// pass-through -- no hidden state, no Win32, no live tray needed to test menu behavior. Mirrors
/// <see cref="WorkspaceSessionAdapter"/>'s injected-<c>Func</c> single-source-of-truth idiom.
/// </summary>
public sealed class TrayMenuControllerTests
{
    [Fact]
    public void TogglePause_FlipsFromFalseToTrue_AndReturnsNewState()
    {
        var paused = false;
        var controller = new TrayMenuController(() => paused, value => paused = value, () => true, _ => { }, () => { }, () => { });

        var result = controller.TogglePause();

        Assert.True(result);
        Assert.True(paused);
    }

    [Fact]
    public void TogglePause_CalledTwice_ReturnsToOriginalState()
    {
        var paused = false;
        var controller = new TrayMenuController(() => paused, value => paused = value, () => true, _ => { }, () => { }, () => { });

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
        var controller = new TrayMenuController(() => paused, value => paused = value, () => true, _ => { }, () => { }, () => { });
        Assert.False(controller.IsPaused);

        paused = true;

        Assert.True(controller.IsPaused);
    }

    [Fact]
    public void Reload_InvokesInjectedReloadDelegate_ExactlyOnce()
    {
        var reloadCount = 0;
        var controller = new TrayMenuController(() => false, _ => { }, () => true, _ => { }, () => reloadCount++, () => { });

        controller.Reload();

        Assert.Equal(1, reloadCount);
    }

    [Fact]
    public void Exit_InvokesInjectedExitDelegate_ExactlyOnce()
    {
        var exitCount = 0;
        var controller = new TrayMenuController(() => false, _ => { }, () => true, _ => { }, () => { }, () => exitCount++);

        controller.Exit();

        Assert.Equal(1, exitCount);
    }

    /// <summary>
    /// The focus-border item is the same shape as Pausar: it owns nothing, it reports what the
    /// injected getter says, and flipping it returns the state the caller must now render.
    /// </summary>
    [Fact]
    public void ToggleFocusBorder_FlipsFromTrueToFalse_AndReturnsNewState()
    {
        var enabled = true;
        var controller = new TrayMenuController(
            () => false, _ => { }, () => enabled, value => enabled = value, () => { }, () => { });

        var result = controller.ToggleFocusBorder();

        Assert.False(result);
        Assert.False(enabled);
    }

    [Fact]
    public void ToggleFocusBorder_CalledTwice_ReturnsToOriginalState()
    {
        var enabled = true;
        var controller = new TrayMenuController(
            () => false, _ => { }, () => enabled, value => enabled = value, () => { }, () => { });

        controller.ToggleFocusBorder();
        var result = controller.ToggleFocusBorder();

        Assert.True(result);
        Assert.True(enabled);
    }

    /// <summary>
    /// No hidden state here either. The setting is persisted to disk and can be changed by something
    /// other than this menu, so the item has to read the world rather than remember it.
    /// </summary>
    [Fact]
    public void IsFocusBorderEnabled_ReflectsInjectedGetter_NotInternalState()
    {
        var enabled = true;
        var controller = new TrayMenuController(
            () => false, _ => { }, () => enabled, value => enabled = value, () => { }, () => { });
        Assert.True(controller.IsFocusBorderEnabled);

        enabled = false;

        Assert.False(controller.IsFocusBorderEnabled);
    }

    /// <summary>The two toggles are independent: neither may move the other.</summary>
    [Fact]
    public void TheTwoTogglesDoNotDisturbEachOther()
    {
        var paused = false;
        var enabled = true;
        var controller = new TrayMenuController(
            () => paused, value => paused = value, () => enabled, value => enabled = value,
            () => { }, () => { });

        controller.TogglePause();
        Assert.True(enabled);

        controller.ToggleFocusBorder();
        Assert.True(paused);
    }
}
