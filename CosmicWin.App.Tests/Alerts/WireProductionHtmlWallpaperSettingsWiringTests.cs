using System.Runtime.CompilerServices;

namespace CosmicWin.App.Tests.Alerts;

/// <summary>
/// D6d (html-wallpaper-demo, demo-only setting, 2026-09-27): <c>AppComposition.WireProduction</c>
/// must forward <c>settings.WallpaperScene</c>/<c>settings.WallpaperFps</c> into the
/// <c>WebViewAlertLayerController</c> it constructs, the same way it already forwards
/// <c>settings.WallpaperMode</c> (D3) as <c>htmlWallpaperMode</c>.
/// </summary>
/// <remarks>
/// <c>WireProduction</c> constructs real Win32/WebView2 collaborators (<see
/// cref="Win32VideoWallpaperHost"/>, a real settings file load, ...) and is not exercised directly by
/// any test in this project -- <c>AppEntryPointThinnessTests</c> established the same precedent for
/// <c>App.xaml.cs</c>'s own call into it: a source-text check is what can be honestly claimed here,
/// not a behavioural guarantee. The real behavioural coverage for what the CONTROLLER does with these
/// two values lives in <c>WebViewAlertLayerControllerTests.SceneFolderName_MapsEachSceneToItsFixedFolderName</c>
/// and <c>WebViewAlertLayerControllerTests.HtmlWallpaperMode_MapsAndNavigatesToTheConfiguredScenePageUnderItsOwnDomain</c>.
/// </remarks>
public sealed class WireProductionHtmlWallpaperSettingsWiringTests
{
    private static string ReadAppCompositionSource([CallerFilePath] string testFilePath = "")
    {
        var testProjectDir = Path.GetDirectoryName(testFilePath)!;
        var path = Path.GetFullPath(Path.Combine(testProjectDir, "..", "..", "CosmicWin.App", "AppComposition.cs"));
        return File.ReadAllText(path);
    }

    [Fact]
    public void WireProduction_PassesWallpaperSceneAndFpsIntoTheAlertLayerController()
    {
        var source = ReadAppCompositionSource();

        var start = source.IndexOf("new WebViewAlertLayerController(", StringComparison.Ordinal);
        Assert.True(start >= 0, "expected a `new WebViewAlertLayerController(...)` call in AppComposition.cs");
        // Anchored on the LAST constructor argument rather than a bare ")" search, which line-ending
        // differences (CRLF) make fragile.
        const string closeMarker = "htmlWallpaperFps: settings.WallpaperFps)";
        var closeIndex = source.IndexOf(closeMarker, start, StringComparison.Ordinal);
        Assert.True(closeIndex > start,
            "expected the WebViewAlertLayerController construction call to end with htmlWallpaperFps: settings.WallpaperFps)");
        var call = source[start..(closeIndex + closeMarker.Length)];

        // D3 (unchanged by D6d).
        Assert.Contains("htmlWallpaperMode: settings.WallpaperMode == WallpaperMode.Html", call);

        // D6d, new.
        Assert.Contains("htmlWallpaperScene: settings.WallpaperScene", call);
        Assert.Contains("htmlWallpaperFps: settings.WallpaperFps", call);
    }
}
