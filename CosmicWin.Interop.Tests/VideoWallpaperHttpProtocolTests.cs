using CosmicWin.Interop;

namespace CosmicWin.Interop.Tests;

/// <summary>
/// Pure rules for the HTTP video-wallpaper endpoint (feature video-wallpaper-http-endpoint, V2): how
/// a JSON body becomes a validated absolute path, and how that validation outcome becomes a status
/// code. Server routing, composition wiring and hardware behaviour are NOT checked here.
/// </summary>
public sealed class VideoWallpaperHttpProtocolTests
{
    private const string ExistingFile = @"C:\videos\x.mp4";

    [Fact]
    public void Constants_MatchThePlan()
    {
        Assert.Equal("/v1/wallpaper/video", VideoWallpaperHttpProtocol.VideoPath);
        Assert.Equal(4096, VideoWallpaperHttpProtocol.MaxBodyBytes);
        Assert.Equal(1024, AlertHttpProtocol.MaxBodyBytes);
        Assert.Equal(503, VideoWallpaperHttpProtocol.NotAvailableStatusCode);
    }

    [Fact]
    public void TryValidate_AnExistingLocalMp4_IsAcceptedWithTheFullPath()
    {
        var probes = FakeProbes(fileExists: true, isDirectory: false, isNetwork: false);

        var outcome = VideoWallpaperHttpProtocol.TryValidate(
            Body(ExistingFile), out var path, out var error, probes);

        Assert.Equal(VideoWallpaperRequestOutcome.Accepted, outcome);
        Assert.Equal(202, VideoWallpaperHttpProtocol.StatusCodeFor(outcome));
        Assert.Equal(ExistingFile, path);
        Assert.Null(error);
    }

    [Fact]
    public void TryValidate_CaseInsensitiveExtension_IsAccepted()
    {
        var probes = FakeProbes(fileExists: true, isDirectory: false, isNetwork: false);

        var outcome = VideoWallpaperHttpProtocol.TryValidate(
            Body(@"C:\videos\X.MP4"), out var path, out var error, probes);

        Assert.Equal(VideoWallpaperRequestOutcome.Accepted, outcome);
        Assert.Equal(@"C:\videos\X.MP4", path);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{")]
    public void TryValidate_MalformedJson_Is400(string body)
    {
        var outcome = VideoWallpaperHttpProtocol.TryValidate(body, out var path, out var error, FakeProbes());

        Assert.Equal(VideoWallpaperRequestOutcome.BadRequest, outcome);
        Assert.Equal(400, VideoWallpaperHttpProtocol.StatusCodeFor(outcome));
        Assert.Null(path);
        Assert.Equal("body is not valid JSON", error);
    }

    [Fact]
    public void TryValidate_ANullBody_Is400WithInvalidJson()
    {
        var outcome = VideoWallpaperHttpProtocol.TryValidate(null, out var path, out var error, FakeProbes());

        Assert.Equal(VideoWallpaperRequestOutcome.BadRequest, outcome);
        Assert.Null(path);
        Assert.Equal("body is not valid JSON", error);
    }

    [Theory]
    [InlineData("[1]")]
    [InlineData("2")]
    [InlineData("null")]
    [InlineData(@"""C:\\videos\\x.mp4""")]
    public void TryValidate_ANonObjectRoot_Is400(string body)
    {
        var outcome = VideoWallpaperHttpProtocol.TryValidate(body, out var path, out var error, FakeProbes());

        Assert.Equal(VideoWallpaperRequestOutcome.BadRequest, outcome);
        Assert.Null(path);
        Assert.Equal("body must be a JSON object", error);
    }

    [Fact]
    public void TryValidate_MissingPathField_Is400()
    {
        var outcome = VideoWallpaperHttpProtocol.TryValidate("{}", out var path, out var error, FakeProbes());

        Assert.Equal(VideoWallpaperRequestOutcome.BadRequest, outcome);
        Assert.Null(path);
        Assert.Equal("field 'path' is required", error);
    }

    [Theory]
    [InlineData("""{"path":123}""")]
    [InlineData("""{"path":true}""")]
    [InlineData("""{"path":null}""")]
    public void TryValidate_PathNotAString_Is400(string body)
    {
        var outcome = VideoWallpaperHttpProtocol.TryValidate(body, out var path, out var error, FakeProbes());

        Assert.Equal(VideoWallpaperRequestOutcome.BadRequest, outcome);
        Assert.Null(path);
        Assert.Equal("field 'path' must be a string", error);
    }

    [Fact]
    public void TryValidate_AnEmptyPath_Is400()
    {
        var outcome = VideoWallpaperHttpProtocol.TryValidate(Body(""), out var path, out var error, FakeProbes());

        Assert.Equal(VideoWallpaperRequestOutcome.BadRequest, outcome);
        Assert.Null(path);
        Assert.Equal("field 'path' must not be empty", error);
    }

    [Fact]
    public void TryValidate_AnUnknownField_Is400AndNamesIt()
    {
        var outcome = VideoWallpaperHttpProtocol.TryValidate(
            """{"path":"C:\\videos\\x.mp4","extra":1}""", out var path, out var error, FakeProbes());

        Assert.Equal(VideoWallpaperRequestOutcome.BadRequest, outcome);
        Assert.Null(path);
        Assert.Equal("unknown field 'extra'", error);
    }

    [Fact]
    public void TryValidate_ADuplicatePathKey_TheLastOneWins()
    {
        var probes = FakeProbes(fileExists: true, isDirectory: false, isNetwork: false);

        var outcome = VideoWallpaperHttpProtocol.TryValidate(
            """{"path":"C:\\videos\\a.mp4","path":"C:\\videos\\x.mp4"}""", out var path, out var error, probes);

        Assert.Equal(VideoWallpaperRequestOutcome.Accepted, outcome);
        Assert.Equal(@"C:\videos\x.mp4", path);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("videos\\x.mp4")]
    [InlineData("C:foo\\x.mp4")]
    [InlineData("\\foo\\x.mp4")]
    [InlineData("\\\\server\\share\\x.mp4")]
    [InlineData("\\\\.\\C:\\x.mp4")]
    public void TryValidate_NotAbsoluteOrDriveRooted_Is400(string rawPath)
    {
        var outcome = VideoWallpaperHttpProtocol.TryValidate(Body(rawPath), out var path, out var error, FakeProbes());

        Assert.Equal(VideoWallpaperRequestOutcome.BadRequest, outcome);
        Assert.Null(path);
        Assert.Equal("path must be an absolute, drive-rooted local path (for example C:\\videos\\x.mp4)", error);
    }

    [Fact]
    public void TryValidate_ADeviceQuestionMarkPath_Is400ForInvalidCharactersFirst()
    {
        // "\\?\C:\x.mp4" is also not drive-rooted, but the literal '?' is caught by the cheaper
        // invalid-characters check first -- both are 400, and this pins the more specific message.
        var outcome = VideoWallpaperHttpProtocol.TryValidate(
            Body("\\\\?\\C:\\x.mp4"), out var path, out var error, FakeProbes());

        Assert.Equal(VideoWallpaperRequestOutcome.BadRequest, outcome);
        Assert.Null(path);
        Assert.Equal("path contains invalid characters", error);
    }

    [Theory]
    [InlineData("http://example.com/x.mp4")]
    [InlineData("https://example.com/x.mp4")]
    [InlineData("file:///C:/videos/x.mp4")]
    public void TryValidate_AUriScheme_Is400WithASpecificReason(string rawPath)
    {
        var outcome = VideoWallpaperHttpProtocol.TryValidate(Body(rawPath), out var path, out var error, FakeProbes());

        Assert.Equal(VideoWallpaperRequestOutcome.BadRequest, outcome);
        Assert.Null(path);
        Assert.Equal("path must be a local file path, not a URI", error);
    }

    [Fact]
    public void TryValidate_AForwardSlashDrivePath_IsRejected()
    {
        // Decided: forward-slash drive paths are rejected, not normalised. See the class remarks in
        // VideoWallpaperHttpProtocol for why (flagged for the maintainer in the task report).
        var outcome = VideoWallpaperHttpProtocol.TryValidate(
            Body("C:/videos/x.mp4"), out var path, out var error, FakeProbes());

        Assert.Equal(VideoWallpaperRequestOutcome.BadRequest, outcome);
        Assert.Null(path);
        Assert.Equal("path must be an absolute, drive-rooted local path (for example C:\\videos\\x.mp4)", error);
    }

    [Theory]
    [InlineData("C:\\videos\\x<1>.mp4")]
    [InlineData("C:\\videos\\x\"y.mp4")]
    [InlineData("C:\\videos\\x|y.mp4")]
    [InlineData("C:\\videos\\x?y.mp4")]
    [InlineData("C:\\videos\\x*y.mp4")]
    // A colon past the drive letter names an NTFS alternate data stream, not a file.
    [InlineData("C:\\videos\\clip.txt:hidden.mp4")]
    [InlineData("C:\\videos\\x.mp4:stream.mp4")]
    public void TryValidate_InvalidPathCharacters_Is400(string rawPath)
    {
        var outcome = VideoWallpaperHttpProtocol.TryValidate(
            Body(rawPath), out var path, out var error, FakeProbes(fileExists: true, isDirectory: false, isNetwork: false));

        Assert.Equal(VideoWallpaperRequestOutcome.BadRequest, outcome);
        Assert.Null(path);
        Assert.Equal("path contains invalid characters", error);
    }

    [Fact]
    public void TryValidate_ANetworkDrive_Is400()
    {
        var probes = FakeProbes(fileExists: true, isDirectory: false, isNetwork: true);

        var outcome = VideoWallpaperHttpProtocol.TryValidate(Body(ExistingFile), out var path, out var error, probes);

        Assert.Equal(VideoWallpaperRequestOutcome.BadRequest, outcome);
        Assert.Null(path);
        Assert.Equal("path must not be on a network drive", error);
    }

    [Fact]
    public void TryValidate_AMissingFile_Is404()
    {
        var probes = FakeProbes(fileExists: false, isDirectory: false, isNetwork: false);

        var outcome = VideoWallpaperHttpProtocol.TryValidate(Body(ExistingFile), out var path, out var error, probes);

        Assert.Equal(VideoWallpaperRequestOutcome.NotFound, outcome);
        Assert.Equal(404, VideoWallpaperHttpProtocol.StatusCodeFor(outcome));
        Assert.Null(path);
        Assert.Equal("file does not exist", error);
    }

    [Fact]
    public void TryValidate_ADirectoryNamedWithMp4Extension_Is404NotAccepted()
    {
        var probes = FakeProbes(fileExists: false, isDirectory: true, isNetwork: false);

        var outcome = VideoWallpaperHttpProtocol.TryValidate(
            Body(@"C:\videos\x.mp4"), out var path, out var error, probes);

        Assert.Equal(VideoWallpaperRequestOutcome.NotFound, outcome);
        Assert.Null(path);
        Assert.Equal("file does not exist", error);
    }

    [Fact]
    public void TryValidate_WrongExtension_Is415()
    {
        var probes = FakeProbes(fileExists: true, isDirectory: false, isNetwork: false);

        var outcome = VideoWallpaperHttpProtocol.TryValidate(
            Body(@"C:\videos\x.avi"), out var path, out var error, probes);

        Assert.Equal(VideoWallpaperRequestOutcome.UnsupportedMediaType, outcome);
        Assert.Equal(415, VideoWallpaperHttpProtocol.StatusCodeFor(outcome));
        Assert.Null(path);
        Assert.Equal("file must have the .mp4 extension", error);
    }

    [Fact]
    public void TryValidate_AnMp4TxtFile_Is415()
    {
        var probes = FakeProbes(fileExists: true, isDirectory: false, isNetwork: false);

        var outcome = VideoWallpaperHttpProtocol.TryValidate(
            Body(@"C:\videos\x.mp4.txt"), out var path, out var error, probes);

        Assert.Equal(VideoWallpaperRequestOutcome.UnsupportedMediaType, outcome);
        Assert.Null(path);
        Assert.Equal("file must have the .mp4 extension", error);
    }

    [Fact]
    public void TryValidate_AMissingNonMp4File_Is415NotFoundDeterministically()
    {
        // Extension is checked before existence, so a wrong-extension request against a file that
        // does not exist still answers 415, never 404 -- deterministically, regardless of whether
        // the file happens to be there.
        var missingProbes = FakeProbes(fileExists: false, isDirectory: false, isNetwork: false);
        var existingProbes = FakeProbes(fileExists: true, isDirectory: false, isNetwork: false);

        var missingOutcome = VideoWallpaperHttpProtocol.TryValidate(
            Body(@"C:\videos\x.txt"), out _, out var missingError, missingProbes);
        var existingOutcome = VideoWallpaperHttpProtocol.TryValidate(
            Body(@"C:\videos\x.txt"), out _, out var existingError, existingProbes);

        Assert.Equal(VideoWallpaperRequestOutcome.UnsupportedMediaType, missingOutcome);
        Assert.Equal(VideoWallpaperRequestOutcome.UnsupportedMediaType, existingOutcome);
        Assert.Equal(missingError, existingError);
        Assert.Equal("file must have the .mp4 extension", missingError);
    }

    [Fact]
    public void TryValidate_AVeryLongPathWithinTheBodyLimit_IsAccepted()
    {
        var longSegment = new string('a', 200);
        var longPath = $@"C:\videos\{longSegment}\{longSegment}\{longSegment}\x.mp4";
        var body = Body(longPath);

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(body) <= VideoWallpaperHttpProtocol.MaxBodyBytes);

        var probes = FakeProbes(fileExists: true, isDirectory: false, isNetwork: false);
        var outcome = VideoWallpaperHttpProtocol.TryValidate(body, out var path, out var error, probes);

        Assert.Equal(VideoWallpaperRequestOutcome.Accepted, outcome);
        Assert.Equal(longPath, path);
        Assert.Null(error);
    }

    [Fact]
    public void TryValidate_JsonEscapedBackslashes_ParseToASingleBackslashPath()
    {
        var probes = FakeProbes(fileExists: true, isDirectory: false, isNetwork: false);

        var outcome = VideoWallpaperHttpProtocol.TryValidate(
            """{"path":"C:\\videos\\x.mp4"}""", out var path, out var error, probes);

        Assert.Equal(VideoWallpaperRequestOutcome.Accepted, outcome);
        Assert.Equal(@"C:\videos\x.mp4", path);
        Assert.Null(error);
    }

    [Fact]
    public void TryValidate_UsingTheRealFilesystem_AnExistingTempFileIsAccepted()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CosmicWinVideoWallpaperProtocolTests");
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(directory, $"{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(filePath, Array.Empty<byte>());

        try
        {
            var outcome = VideoWallpaperHttpProtocol.TryValidate(Body(filePath), out var path, out var error);

            Assert.Equal(VideoWallpaperRequestOutcome.Accepted, outcome);
            Assert.Equal(filePath, path);
            Assert.Null(error);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void TryValidate_UsingTheRealFilesystem_AMissingFileIs404()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp4");

        var outcome = VideoWallpaperHttpProtocol.TryValidate(Body(missingPath), out var path, out var error);

        Assert.Equal(VideoWallpaperRequestOutcome.NotFound, outcome);
        Assert.Null(path);
        Assert.Equal("file does not exist", error);
    }

    [Fact]
    public void NotAvailable_ConstantsAreConsistent()
    {
        Assert.Equal(503, VideoWallpaperHttpProtocol.NotAvailableStatusCode);
        Assert.Equal("video wallpaper is not available on this composition", VideoWallpaperHttpProtocol.NotAvailableError);
    }

    private static string Body(string path) =>
        System.Text.Json.JsonSerializer.Serialize(new { path });

    private static VideoWallpaperFileProbes FakeProbes(
        bool fileExists = false, bool isDirectory = false, bool isNetwork = false) =>
        new(
            fileExists: _ => fileExists,
            directoryExists: _ => isDirectory,
            isNetworkDrive: _ => isNetwork);
}
