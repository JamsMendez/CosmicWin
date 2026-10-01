using CosmicWin.App.Wallpaper;
using Microsoft.Web.WebView2.Core;

namespace CosmicWin.App.Tests.Wallpaper;

/// <summary>
/// Which WebView2 process failures take the mini window's browser down for a re-attach. Every recovery
/// spends part of the controller's small lifetime budget, so a failure that leaves the page working
/// (a utility or GPU helper exiting, a sub-frame renderer) must not burn it.
/// </summary>
public sealed class MiniProcessFailurePolicyTests
{
    [Theory]
    [InlineData(CoreWebView2ProcessFailedKind.BrowserProcessExited)]
    [InlineData(CoreWebView2ProcessFailedKind.RenderProcessExited)]
    [InlineData(CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)]
    [InlineData(CoreWebView2ProcessFailedKind.UnknownProcessExited)]
    public void AFailureThatLeavesThePageUnusableTriggersRecovery(CoreWebView2ProcessFailedKind kind)
    {
        Assert.True(MiniProcessFailurePolicy.RequiresRecovery(kind));
    }

    [Theory]
    [InlineData(CoreWebView2ProcessFailedKind.FrameRenderProcessExited)]
    [InlineData(CoreWebView2ProcessFailedKind.UtilityProcessExited)]
    [InlineData(CoreWebView2ProcessFailedKind.SandboxHelperProcessExited)]
    [InlineData(CoreWebView2ProcessFailedKind.GpuProcessExited)]
    [InlineData(CoreWebView2ProcessFailedKind.PpapiPluginProcessExited)]
    [InlineData(CoreWebView2ProcessFailedKind.PpapiBrokerProcessExited)]
    public void AHelperProcessFailureDoesNotTearTheBrowserDown(CoreWebView2ProcessFailedKind kind)
    {
        Assert.False(MiniProcessFailurePolicy.RequiresRecovery(kind));
    }

    [Fact]
    public void AKindAddedByAFutureRuntimeIsTreatedAsFatalRatherThanIgnored()
    {
        Assert.True(MiniProcessFailurePolicy.RequiresRecovery((CoreWebView2ProcessFailedKind)999));
    }
}
