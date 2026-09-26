using CosmicWin.App.Alerts;
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
/// cref="Direct2DAlertOverlay_NoLongerExistsInCosmicWinInterop"/>'s sibling positive-control fact
/// proves the assembly lookup itself still works, so a broken lookup fails loudly instead of reading
/// as "removed".
/// <para>
/// alert-tile-mosaic (2026-09-26) deliberately REINTRODUCES <c>CosmicWin.App.Alerts.AlertTileLayout</c>
/// under the same name this removed Direct2D tile-grid layout used, for an unrelated, live purpose: a
/// small pure record (ordered tile kinds + grid) computed from an <see cref="AlertCommand"/>, wired
/// into <c>AppComposition.UpdateAlertOverlay</c> and unit-tested directly by
/// <c>AlertTileLayoutTests</c> -- not the removed Direct2D consumer coming back. The old "no longer
/// exists" assertion for that name was therefore REPLACED with <see
/// cref="AlertTileLayout_IsTheNewLiveMosaicLayoutNotTheRemovedDirect2DOne"/> below, rather than
/// silently dropped.
/// </para>
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

    /// <summary>
    /// Replaces the old "no longer exists" assertion for this name (see class remarks,
    /// alert-tile-mosaic 2026-09-26): the name is deliberately back, but as a small, pure, live-wired
    /// record with a static <c>From</c> factory taking an <see cref="AlertCommand"/> -- not the
    /// removed Direct2D tile-grid consumer, which had no such factory at all.
    /// </summary>
    [Fact]
    public void AlertTileLayout_IsTheNewLiveMosaicLayoutNotTheRemovedDirect2DOne()
    {
        var type = AppAssembly.GetType("CosmicWin.App.Alerts.AlertTileLayout");
        Assert.NotNull(type);

        var from = type!.GetMethod("From", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        Assert.NotNull(from);
        Assert.Equal(typeof(AlertCommand), Assert.Single(from!.GetParameters()).ParameterType);
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
