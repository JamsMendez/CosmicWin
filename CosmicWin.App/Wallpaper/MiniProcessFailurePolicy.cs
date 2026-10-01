using Microsoft.Web.WebView2.Core;

namespace CosmicWin.App.Wallpaper;

/// <summary>
/// Decides which WebView2 process failures make the mini window drop its browser and re-attach. Each
/// re-attach spends part of <see cref="MiniSceneWindowController"/>'s small lifetime budget, so only a
/// failure that leaves the page unusable may spend it: the browser or the page's own renderer going away
/// (or hanging), or a kind this build does not know. A helper process exiting (utility, GPU, sandbox
/// helper, plugin, a sub-frame renderer) is restarted by WebView2 itself and the scene keeps running.
/// </summary>
public static class MiniProcessFailurePolicy
{
    public static bool RequiresRecovery(CoreWebView2ProcessFailedKind kind) => kind switch
    {
        CoreWebView2ProcessFailedKind.FrameRenderProcessExited
            or CoreWebView2ProcessFailedKind.UtilityProcessExited
            or CoreWebView2ProcessFailedKind.SandboxHelperProcessExited
            or CoreWebView2ProcessFailedKind.GpuProcessExited
            or CoreWebView2ProcessFailedKind.PpapiPluginProcessExited
            or CoreWebView2ProcessFailedKind.PpapiBrokerProcessExited => false,
        // BrowserProcessExited, RenderProcessExited, RenderProcessUnresponsive, UnknownProcessExited and any
        // kind a newer runtime adds: assume the page is gone rather than leave the window blank.
        _ => true,
    };
}
