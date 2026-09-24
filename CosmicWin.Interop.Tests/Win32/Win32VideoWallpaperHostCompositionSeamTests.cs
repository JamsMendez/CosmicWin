using CosmicWin.Interop.Win32;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// T2 (webview-alert-layer): the composition seam <see cref="Win32VideoWallpaperHost"/> exposes for
/// T3's WebView2 layer to add one overlay visual above the video, remove it, commit, and detect a
/// host-window recreate -- a clean public surface, not an <c>InternalsVisibleTo</c> hack, since T3
/// lives in <c>CosmicWin.App</c>, which this assembly does not otherwise expose internals to.
/// </summary>
/// <remarks>
/// Pure state checks only, before any real attach -- no desktop session needed. The desktop-gated
/// exercise of the actual DirectComposition tree (built after attach, overlay add/remove against a
/// real device, rebuild after the host window is destroyed, and the cross-thread access from an STA
/// thread that the T0 spike proved breaks without care) lives in
/// <see cref="Win32VideoWallpaperHostRealAttachTests"/>.
/// </remarks>
public sealed class Win32VideoWallpaperHostCompositionSeamTests
{
    [Fact]
    public void IsCompositionReady_IsFalseBeforeAttach()
    {
        using var host = new Win32VideoWallpaperHost();
        Assert.False(host.IsCompositionReady);
    }

    [Fact]
    public void CompositionGeneration_StartsAtZeroBeforeAttach()
    {
        using var host = new Win32VideoWallpaperHost();
        Assert.Equal(0, host.CompositionGeneration);
    }

    /// <summary>
    /// Promoted from an internal, test-only member to a public one by this task: T3 needs the real
    /// HWND to create its <c>CoreWebView2CompositionController</c> against, from a different
    /// assembly this one does not grant <c>InternalsVisibleTo</c> to.
    /// </summary>
    [Fact]
    public void Hwnd_IsZeroBeforeAttach()
    {
        using var host = new Win32VideoWallpaperHost();
        Assert.Equal(0, host.Hwnd);
    }

    [Fact]
    public void AddCompositionOverlayVisual_ReturnsNullBeforeTheCompositionTreeExists()
    {
        using var host = new Win32VideoWallpaperHost();
        Assert.Null(host.AddCompositionOverlayVisual());
    }

    /// <summary>Never throws: a DComp failure (or nothing to remove) must never reach the caller.</summary>
    [Fact]
    public void RemoveCompositionOverlayVisual_IsANoOpWhenNothingWasEverAdded()
    {
        using var host = new Win32VideoWallpaperHost();
        var exception = Record.Exception(host.RemoveCompositionOverlayVisual);
        Assert.Null(exception);
    }

    /// <summary>Never throws: the whole point of this seam is that DComp failures stay contained.</summary>
    [Fact]
    public void CommitComposition_IsANoOpBeforeTheDeviceExists()
    {
        using var host = new Win32VideoWallpaperHost();
        var exception = Record.Exception(host.CommitComposition);
        Assert.Null(exception);
    }
}
