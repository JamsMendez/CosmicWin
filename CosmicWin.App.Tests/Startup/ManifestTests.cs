using System.Runtime.CompilerServices;

namespace CosmicWin.App.Tests.Startup;

/// <summary>Manifest CONTENT and csproj WIRING as source-text facts; real effect is only observable on a real elevated/non-elevated logon (ES-3, human-only).</summary>
public sealed class ManifestTests
{
    private static string ReadSibling(string relativePath, [CallerFilePath] string testFilePath = "")
    {
        var testProjectDir = Path.GetDirectoryName(Path.GetDirectoryName(testFilePath))!;
        return File.ReadAllText(Path.GetFullPath(Path.Combine(testProjectDir, relativePath)));
    }

    [Theory]
    [InlineData(@"..\CosmicWin.App\app.manifest", @"level=""requireAdministrator""")]
    [InlineData(@"..\CosmicWin.Launcher\app.manifest", @"level=""asInvoker""")]
    public void Manifest_DeclaresExpectedExecutionLevel(string relativePath, string expected)
    {
        Assert.Contains(expected, ReadSibling(relativePath));
    }

    [Theory]
    [InlineData(@"..\CosmicWin.App\CosmicWin.App.csproj")]
    [InlineData(@"..\CosmicWin.Launcher\CosmicWin.Launcher.csproj")]
    public void Csproj_WiresAppManifest(string relativePath)
    {
        Assert.Contains("<ApplicationManifest>app.manifest</ApplicationManifest>", ReadSibling(relativePath));
    }
}
