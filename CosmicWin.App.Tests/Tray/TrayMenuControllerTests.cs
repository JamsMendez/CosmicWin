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

    /// <summary>
    /// Read through the getter like every other item here, because the colour is persisted and the
    /// settings file can be edited by hand while the menu is closed.
    /// </summary>
    [Fact]
    public void BorderColor_ReflectsInjectedGetter_NotInternalState()
    {
        uint? colour = 0xFF8800u;
        var controller = new TrayMenuController(
            () => false, _ => { }, () => true, _ => { }, () => { }, () => { },
            () => colour, value => colour = value);

        Assert.Equal(0xFF8800u, controller.BorderColor);

        colour = null;

        Assert.Null(controller.BorderColor);
    }

    /// <summary>
    /// Forwarded, never interpreted. The controller does not know what a colour looks like on
    /// screen and must not start deciding which ones are allowed.
    /// </summary>
    [Fact]
    public void SetBorderColor_ForwardsBothAColourAndTheAccent()
    {
        var chosen = new List<uint?>();
        var controller = new TrayMenuController(
            () => false, _ => { }, () => true, _ => { }, () => { }, () => { },
            () => null, chosen.Add);

        controller.SetBorderColor(0x00A0FFu);
        controller.SetBorderColor(null);

        Assert.Equal([0x00A0FFu, null], chosen);
    }

    /// <summary>
    /// The colour delegates are optional, because every caller that predates them passes six
    /// arguments. Without them the item reports the accent and setting one is a quiet no-op --
    /// never a throw from a menu click.
    /// </summary>
    [Fact]
    public void WithNoColourDelegatesWired_TheItemIsInertRatherThanBroken()
    {
        var controller = new TrayMenuController(
            () => false, _ => { }, () => true, _ => { }, () => { }, () => { });

        Assert.Null(controller.BorderColor);

        controller.SetBorderColor(0xFF0000u);
    }

    /// <summary>
    /// The tiling switch is the same shape as the other two: it owns nothing, it reports what the
    /// injected getter says, and flipping it returns the state the caller must now render.
    /// </summary>
    [Fact]
    public void ToggleTiling_FlipsFromTrueToFalse_AndReturnsNewState()
    {
        var tiling = true;
        var controller = new TrayMenuController(
            () => false, _ => { }, () => true, _ => { }, () => { }, () => { },
            null, null, () => tiling, value => tiling = value);

        var result = controller.ToggleTiling();

        Assert.False(result);
        Assert.False(tiling);
    }

    [Fact]
    public void ToggleTiling_CalledTwice_ReturnsToOriginalState()
    {
        var tiling = true;
        var controller = new TrayMenuController(
            () => false, _ => { }, () => true, _ => { }, () => { }, () => { },
            null, null, () => tiling, value => tiling = value);

        controller.ToggleTiling();
        var result = controller.ToggleTiling();

        Assert.True(result);
        Assert.True(tiling);
    }

    /// <summary>No hidden state here either: the switch is persisted and can be edited by hand.</summary>
    [Fact]
    public void IsTilingEnabled_ReflectsInjectedGetter_NotInternalState()
    {
        var tiling = true;
        var controller = new TrayMenuController(
            () => false, _ => { }, () => true, _ => { }, () => { }, () => { },
            null, null, () => tiling, value => tiling = value);
        Assert.True(controller.IsTilingEnabled);

        tiling = false;

        Assert.False(controller.IsTilingEnabled);
    }

    /// <summary>
    /// Turning tiling off must not pause CosmicWin, and pausing must not turn tiling off. They are
    /// two different sizes of the same idea and the whole point of the new item is that they differ.
    /// </summary>
    [Fact]
    public void TheTilingSwitchAndThePause_DoNotDisturbEachOther()
    {
        var paused = false;
        var tiling = true;
        var controller = new TrayMenuController(
            () => paused, value => paused = value, () => true, _ => { }, () => { }, () => { },
            null, null, () => tiling, value => tiling = value);

        controller.ToggleTiling();
        Assert.False(paused);

        controller.TogglePause();
        Assert.False(tiling);
    }

    /// <summary>
    /// Unwired -- as every caller that predates the switch is -- the app tiles, and the toggle
    /// reports the state it could not change rather than a tick that lies about the world.
    /// </summary>
    [Fact]
    public void WithNoTilingDelegatesWired_TheItemIsInertRatherThanBroken()
    {
        var controller = new TrayMenuController(
            () => false, _ => { }, () => true, _ => { }, () => { }, () => { });

        Assert.True(controller.IsTilingEnabled);
        Assert.True(controller.ToggleTiling());
        Assert.True(controller.IsTilingEnabled);
    }

    /// <summary>
    /// Write-only, unlike <see cref="TrayMenuController.BorderColor"/>: nothing in the tray menu
    /// needs to read back or display the current video path, so there is no getter to prove
    /// alongside this one.
    /// </summary>
    [Fact]
    public void SetVideoWallpaperPath_InvokesInjectedDelegate_WithTheGivenPath()
    {
        var chosen = new List<string>();
        var controller = new TrayMenuController(
            () => false, _ => { }, () => true, _ => { }, () => { }, () => { },
            setVideoWallpaperPath: chosen.Add);

        controller.SetVideoWallpaperPath(@"C:\videos\clip.mp4");

        Assert.Equal([@"C:\videos\clip.mp4"], chosen);
    }

    /// <summary>
    /// Unwired -- as every caller that predates this switch is -- a menu click must never throw.
    /// </summary>
    [Fact]
    public void WithNoVideoWallpaperDelegateWired_SetVideoWallpaperPath_DoesNotThrow()
    {
        var controller = new TrayMenuController(
            () => false, _ => { }, () => true, _ => { }, () => { }, () => { });

        controller.SetVideoWallpaperPath(@"C:\videos\clip.mp4");
    }
}
