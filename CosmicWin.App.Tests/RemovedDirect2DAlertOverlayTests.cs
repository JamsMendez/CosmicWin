namespace CosmicWin.App.Tests;

/// <summary>
/// T1 (remove-direct2d-alert-overlay): the Direct2D alert renderer, the frame-overlay seam it drew
/// through, and the tile-grid layout it consumed were unwired dead code -- <c>AppComposition.
/// WireProduction</c> never passed <c>alertOverlay</c>/<c>clearAlertOverlay</c>/
/// <c>setAlertOverlayTiles</c> to <c>Wire</c>, and the preloaded WebView2 alert layer
/// (<c>startAlertLayer</c>) is the only path a real user ever reaches. This is a structural
/// regression guard, the same acknowledged-limitation shape as <see
/// cref="AppEntryPointThinnessTests"/>: it proves the types and parameters are GONE, not that
/// anything still works -- behavior stays proven by <c>WebViewAlertCompositionWiringTests</c> and
/// <c>AlertDesktopVisibilityWiringTests</c>.
/// </summary>
public sealed class RemovedDirect2DAlertOverlayTests
{
    [Fact]
    public void Direct2DAlertOverlay_NoLongerExistsInCosmicWinInterop()
    {
        Assert.Null(Type.GetType("CosmicWin.Interop.Win32.Direct2DAlertOverlay, CosmicWin.Interop"));
    }

    [Fact]
    public void IFrameOverlay_NoLongerExistsInCosmicWinInterop()
    {
        Assert.Null(Type.GetType("CosmicWin.Interop.IFrameOverlay, CosmicWin.Interop"));
    }

    [Fact]
    public void FrameOverlayTile_NoLongerExistsInCosmicWinInterop()
    {
        Assert.Null(Type.GetType("CosmicWin.Interop.FrameOverlayTile, CosmicWin.Interop"));
    }

    [Fact]
    public void FrameOverlayTileKind_NoLongerExistsInCosmicWinInterop()
    {
        Assert.Null(Type.GetType("CosmicWin.Interop.FrameOverlayTileKind, CosmicWin.Interop"));
    }

    [Fact]
    public void AlertTileLayout_NoLongerExistsInCosmicWinApp()
    {
        Assert.Null(Type.GetType("CosmicWin.App.Alerts.AlertTileLayout, CosmicWin.App"));
    }

    [Fact]
    public void AppCompositionWire_HasNoDirect2DOverlayParameters()
    {
        var wire = typeof(AppComposition).GetMethod("Wire", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        Assert.NotNull(wire);

        var parameterNames = wire!.GetParameters().Select(p => p.Name).ToArray();

        Assert.DoesNotContain("alertOverlay", parameterNames);
        Assert.DoesNotContain("clearAlertOverlay", parameterNames);
        Assert.DoesNotContain("setAlertOverlayTiles", parameterNames);
    }
}
