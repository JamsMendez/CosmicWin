using System.Runtime.CompilerServices;

namespace CosmicWin.App.Tests.Wallpaper;

/// <summary>
/// Structural guards for the hardware-only WebView2 shell (a real environment cannot exist in a unit
/// test), in the style of the alert layer's own guards. The recovery policy itself is behavior-tested
/// through the seam in <see cref="MiniSceneWindowControllerTests"/>.
/// </summary>
public sealed class WebView2MiniSceneBrowserSourceGuardTests
{
    [Fact]
    public void ProcessFailureIsTracedThenTheControllerIsReleasedAndTheOwnerToldToRecover()
    {
        var source = ReadSource();

        Assert.Contains("candidate.CoreWebView2.ProcessFailed += OnProcessFailed;", source);
        Assert.Contains("controller.CoreWebView2.ProcessFailed -= OnProcessFailed;", source);
        Assert.Contains("AlertLayerTrace.ProcessFailed(", source);
        Assert.Contains("Failed?.Invoke(reason);", source);
    }

    [Fact]
    public void APartialAttachReleasesTheControllerTheVisualAndTheEnvironment()
    {
        var source = ReadSource();
        var finallyAt = source.IndexOf("finally", StringComparison.Ordinal);

        Assert.True(finallyAt > 0, "AttachAsync must clean up in a finally.");
        var cleanup = source[finallyAt..];
        Assert.Contains("candidate.Close();", cleanup);
        Assert.Contains("surface.RemoveCompositionOverlayVisual();", cleanup);
        Assert.Contains("_environment = null;", cleanup);
    }

    private static string ReadSource([CallerFilePath] string testFilePath = "")
    {
        var dir = Path.GetDirectoryName(testFilePath)!;
        return File.ReadAllText(Path.GetFullPath(
            Path.Combine(dir, "..", "..", "CosmicWin.App", "Wallpaper", "WebView2MiniSceneBrowser.cs")));
    }
}
