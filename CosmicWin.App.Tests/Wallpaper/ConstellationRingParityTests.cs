namespace CosmicWin.App.Tests.Wallpaper;

/// <summary>
/// The idle and explorer scenes each ship their own copy of the constellation ring (the
/// CONSTELLATION_FIGURES table and the code that draws it in <c>js/rings.js</c>, plus its
/// CONSTELLATION_* tunables in <c>js/config.js</c>). Nothing else ties the two copies together, so
/// a fix made to one scene could quietly skip the other; these tests make that drift fail loudly.
/// </summary>
/// <remarks>
/// Reads the REAL shipped files from the test output (the same <c>Wallpaper\Web\**</c> content
/// <see cref="IdleSceneNodeTests"/> and <see cref="ExplorerSceneNodeTests"/> load). Only the
/// constellation ring is compared: the rest of each scene is free to differ.
/// </remarks>
public sealed class ConstellationRingParityTests
{
    private const string RingStartMarker = "// --- Ring 3: constellation ring";
    private const string RingEndMarker = "// --- Ring 4:";

    private static readonly string WebDirectory =
        Path.Combine(AppContext.BaseDirectory, "Wallpaper", "Web");

    [Fact]
    public void TheConstellationRingCode_IsTheSameInTheIdleAndExplorerScenes()
    {
        var idle = ConstellationRingSection(ReadScene("idle", "rings.js"));
        var explorer = ConstellationRingSection(ReadScene("explorer", "rings.js"));

        Assert.Equal(idle, explorer);
    }

    [Fact]
    public void TheConstellationTunables_AreTheSameInTheIdleAndExplorerScenes()
    {
        var idle = ConstellationTunables(ReadScene("idle", "config.js"));
        var explorer = ConstellationTunables(ReadScene("explorer", "config.js"));

        Assert.NotEmpty(idle);
        Assert.Equal(idle, explorer);
    }

    private static string ReadScene(string scene, string file)
    {
        var path = Path.Combine(WebDirectory, scene, "js", file);
        Assert.True(File.Exists(path), $"Expected the shipped '{path}' (see CosmicWin.App.csproj's Wallpaper\\Web\\** Content item).");
        return File.ReadAllText(path).ReplaceLineEndings("\n");
    }

    private static string ConstellationRingSection(string source)
    {
        var start = source.IndexOf(RingStartMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected the '{RingStartMarker}' section marker in rings.js.");
        var end = source.IndexOf(RingEndMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Expected the '{RingEndMarker}' marker after the constellation ring in rings.js.");
        return source[start..end];
    }

    private static string[] ConstellationTunables(string source) =>
        source.Split('\n')
            .Where(line => line.StartsWith("const CONSTELLATION_", StringComparison.Ordinal))
            .ToArray();
}
