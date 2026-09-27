using CosmicWin.Interop;

namespace CosmicWin.Interop.Tests;

/// <summary>
/// Pure rules for the HTTP wallpaper-scene endpoint (feature wallpaper-scene-http-endpoint, S1): how
/// a JSON body becomes a validated scene name, and how that validation outcome becomes a status
/// code. Server routing, composition wiring and hardware behaviour are NOT checked here -- the same
/// split <see cref="VideoWallpaperHttpProtocolTests"/> uses for the sibling video route.
/// </summary>
public sealed class WallpaperSceneHttpProtocolTests
{
    [Fact]
    public void Constants_MatchThePlan()
    {
        Assert.Equal("/v1/wallpaper/scene", WallpaperSceneHttpProtocol.ScenePath);
        Assert.Equal(256, WallpaperSceneHttpProtocol.MaxBodyBytes);
        Assert.Equal(503, WallpaperSceneHttpProtocol.NotAvailableStatusCode);
    }

    [Theory]
    [InlineData("processing", "processing")]
    [InlineData("explorer", "explorer")]
    [InlineData("idle", "idle")]
    [InlineData("raphael", "raphael")]
    [InlineData("PROCESSING", "processing")]
    [InlineData("Explorer", "explorer")]
    [InlineData("IdLe", "idle")]
    [InlineData("RAPHAEL", "raphael")]
    public void TryValidate_AKnownScene_IsAcceptedAndNormalizedToLowercase(string rawScene, string expected)
    {
        var outcome = WallpaperSceneHttpProtocol.TryValidate(Body(rawScene), out var scene, out var error);

        Assert.Equal(WallpaperSceneRequestOutcome.Accepted, outcome);
        Assert.Equal(202, WallpaperSceneHttpProtocol.StatusCodeFor(outcome));
        Assert.Equal(expected, scene);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("""{"scene":"idle\uD800"}""")]
    [InlineData("""{"\uD800":"idle"}""")]
    public void TryValidate_MalformedJson_Is400(string body)
    {
        var outcome = WallpaperSceneHttpProtocol.TryValidate(body, out var scene, out var error);

        Assert.Equal(WallpaperSceneRequestOutcome.BadRequest, outcome);
        Assert.Equal(400, WallpaperSceneHttpProtocol.StatusCodeFor(outcome));
        Assert.Null(scene);
        Assert.Equal("body is not valid JSON", error);
    }

    [Fact]
    public void TryValidate_ANullBody_Is400WithInvalidJson()
    {
        var outcome = WallpaperSceneHttpProtocol.TryValidate(null, out var scene, out var error);

        Assert.Equal(WallpaperSceneRequestOutcome.BadRequest, outcome);
        Assert.Null(scene);
        Assert.Equal("body is not valid JSON", error);
    }

    [Theory]
    [InlineData("[1]")]
    [InlineData("2")]
    [InlineData("null")]
    [InlineData("\"idle\"")]
    public void TryValidate_ANonObjectRoot_Is400(string body)
    {
        var outcome = WallpaperSceneHttpProtocol.TryValidate(body, out var scene, out var error);

        Assert.Equal(WallpaperSceneRequestOutcome.BadRequest, outcome);
        Assert.Null(scene);
        Assert.Equal("body must be a JSON object", error);
    }

    [Fact]
    public void TryValidate_MissingSceneField_Is400()
    {
        var outcome = WallpaperSceneHttpProtocol.TryValidate("{}", out var scene, out var error);

        Assert.Equal(WallpaperSceneRequestOutcome.BadRequest, outcome);
        Assert.Null(scene);
        Assert.Equal("field 'scene' is required", error);
    }

    [Theory]
    [InlineData("""{"scene":123}""")]
    [InlineData("""{"scene":true}""")]
    [InlineData("""{"scene":null}""")]
    public void TryValidate_SceneNotAString_Is400(string body)
    {
        var outcome = WallpaperSceneHttpProtocol.TryValidate(body, out var scene, out var error);

        Assert.Equal(WallpaperSceneRequestOutcome.BadRequest, outcome);
        Assert.Null(scene);
        Assert.Equal("field 'scene' must be a string", error);
    }

    [Fact]
    public void TryValidate_AnEmptyScene_Is400()
    {
        var outcome = WallpaperSceneHttpProtocol.TryValidate(Body(""), out var scene, out var error);

        Assert.Equal(WallpaperSceneRequestOutcome.BadRequest, outcome);
        Assert.Null(scene);
        Assert.Equal("field 'scene' must not be empty", error);
    }

    [Fact]
    public void TryValidate_AnUnknownField_Is400AndNamesIt()
    {
        var outcome = WallpaperSceneHttpProtocol.TryValidate(
            """{"scene":"idle","extra":1}""", out var scene, out var error);

        Assert.Equal(WallpaperSceneRequestOutcome.BadRequest, outcome);
        Assert.Null(scene);
        Assert.Equal("unknown field 'extra'", error);
    }

    [Fact]
    public void TryValidate_ADuplicateSceneKey_TheLastOneWins()
    {
        var outcome = WallpaperSceneHttpProtocol.TryValidate(
            """{"scene":"idle","scene":"raphael"}""", out var scene, out var error);

        Assert.Equal(WallpaperSceneRequestOutcome.Accepted, outcome);
        Assert.Equal("raphael", scene);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("wallpaper")]
    [InlineData("video")]
    [InlineData("html")]
    [InlineData("proc essing")]
    [InlineData(" idle")]
    public void TryValidate_AnUnknownSceneName_Is400(string rawScene)
    {
        var outcome = WallpaperSceneHttpProtocol.TryValidate(Body(rawScene), out var scene, out var error);

        Assert.Equal(WallpaperSceneRequestOutcome.BadRequest, outcome);
        Assert.Equal(400, WallpaperSceneHttpProtocol.StatusCodeFor(outcome));
        Assert.Null(scene);
        Assert.Equal("field 'scene' must be one of: processing, explorer, idle, raphael", error);
    }

    [Fact]
    public void NotAvailable_ConstantsAreConsistent()
    {
        Assert.Equal(503, WallpaperSceneHttpProtocol.NotAvailableStatusCode);
        Assert.Equal(
            "wallpaper scene switching is not available on this composition",
            WallpaperSceneHttpProtocol.NotAvailableError);
    }

    private static string Body(string scene) => System.Text.Json.JsonSerializer.Serialize(new { scene });
}
