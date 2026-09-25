using CosmicWin.Interop.Win32;

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
/// <remarks>
/// T1b: the assembly for each removed-type lookup is resolved from a known SURVIVING type in that
/// same assembly (<see cref="MediaFoundationVideoWallpaperPlayer"/> for <c>CosmicWin.Interop</c>,
/// <see cref="AppComposition"/> for <c>CosmicWin.App</c>) rather than by assembly NAME --
/// <c>Type.GetType("Foo, SomeAssembly")</c> also returns null when <c>SomeAssembly</c> itself fails
/// to resolve (a typo'd name, a renamed assembly, one not yet loaded), which would make every
/// "no longer exists" assertion here pass for the wrong reason. <see
/// cref="Direct2DAlertOverlay_NoLongerExistsInCosmicWinInterop"/> and <see
/// cref="AlertTileLayout_NoLongerExistsInCosmicWinApp"/>'s sibling positive-control facts prove the
/// assembly lookup itself still works, so a broken lookup fails loudly instead of reading as
/// "removed".
/// </remarks>
public sealed class RemovedDirect2DAlertOverlayTests
{
    private static readonly System.Reflection.Assembly InteropAssembly =
        typeof(MediaFoundationVideoWallpaperPlayer).Assembly;

    private static readonly System.Reflection.Assembly AppAssembly = typeof(AppComposition).Assembly;

    [Fact]
    public void PositiveControl_TheInteropAssemblyLookupStillFindsASurvivingType()
    {
        Assert.NotNull(InteropAssembly.GetType("CosmicWin.Interop.Win32.MediaFoundationVideoWallpaperPlayer"));
    }

    [Fact]
    public void PositiveControl_TheAppAssemblyLookupStillFindsASurvivingType()
    {
        Assert.NotNull(AppAssembly.GetType("CosmicWin.App.AppComposition"));
    }

    [Fact]
    public void Direct2DAlertOverlay_NoLongerExistsInCosmicWinInterop()
    {
        Assert.Null(InteropAssembly.GetType("CosmicWin.Interop.Win32.Direct2DAlertOverlay"));
    }

    [Fact]
    public void IFrameOverlay_NoLongerExistsInCosmicWinInterop()
    {
        Assert.Null(InteropAssembly.GetType("CosmicWin.Interop.IFrameOverlay"));
    }

    [Fact]
    public void FrameOverlayTile_NoLongerExistsInCosmicWinInterop()
    {
        Assert.Null(InteropAssembly.GetType("CosmicWin.Interop.FrameOverlayTile"));
    }

    [Fact]
    public void FrameOverlayTileKind_NoLongerExistsInCosmicWinInterop()
    {
        Assert.Null(InteropAssembly.GetType("CosmicWin.Interop.FrameOverlayTileKind"));
    }

    [Fact]
    public void AlertTileLayout_NoLongerExistsInCosmicWinApp()
    {
        Assert.Null(AppAssembly.GetType("CosmicWin.App.Alerts.AlertTileLayout"));
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
