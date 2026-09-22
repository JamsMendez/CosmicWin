using CosmicWin.Interop.Win32;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// T4: first real exercise of <see cref="MediaFoundationVideoWallpaperPlayer"/>. There is a
/// genuine, documented gap here -- no sample video file ships in this repo or CI, so nothing here
/// can assert that a real video actually renders correctly or loops smoothly; that is a manual,
/// on-hardware verification (see <c>odd/tasks/video-wallpaper.md</c>, T4). What these tests DO
/// honestly cover: the failure paths that do not require a real video file, and that
/// <see cref="MediaFoundationVideoWallpaperPlayer.Dispose"/> is always safe to call.
/// </summary>
[Trait("Category", "RequiresDesktop")]
[Collection(RealDesktopCollection.Name)]
public sealed class MediaFoundationVideoWallpaperPlayerTests
{
    [RequiresDesktopSessionFact]
    public void TryPlay_NonExistentPath_ReturnsFalseAndDoesNotThrowOrBreakTheEngine()
    {
        using var host = new Win32VideoWallpaperHost();
        Assert.True(host.TryAttach(), "TryAttach should succeed on a real interactive desktop session.");

        using var player = new MediaFoundationVideoWallpaperPlayer();

        string nonExistentPath = Path.Combine(
            Path.GetTempPath(), $"cosmicwin-does-not-exist-{Guid.NewGuid():N}.mp4");

        var firstAttempt = player.TryPlay(host, nonExistentPath);
        Assert.False(firstAttempt);
        Assert.False(player.IsPlayingForTests);

        // Calling again on the same player must be just as graceful -- a failed first attempt
        // must not leave the engine (or the player's own state) broken for the next call.
        var secondAttempt = player.TryPlay(host, nonExistentPath);
        Assert.False(secondAttempt);
        Assert.False(player.IsPlayingForTests);
    }

    [RequiresDesktopSessionFact]
    public void TryPlay_InvalidVideoFile_DoesNotThrowOrCrash()
    {
        using var host = new Win32VideoWallpaperHost();
        Assert.True(host.TryAttach(), "TryAttach should succeed on a real interactive desktop session.");

        string invalidVideoPath = Path.Combine(
            Path.GetTempPath(), $"cosmicwin-invalid-video-{Guid.NewGuid():N}.txt");
        File.WriteAllText(invalidVideoPath, "this is not a video file");

        try
        {
            using var player = new MediaFoundationVideoWallpaperPlayer();

            // IMFMediaEngine::SetSource loads asynchronously, so a syntactically-valid path to
            // content that is not actually a playable video is NOT guaranteed to fail
            // synchronously inside TryPlay -- MF_MEDIA_ENGINE_EVENT_ERROR is documented to arrive
            // later, through the notify callback. What is guaranteed, and what this test asserts,
            // is that TryPlay itself never throws and the process does not crash. See
            // odd/tasks/video-wallpaper.md T4 for why a stronger assertion here would not be
            // honest without a real corrupt-but-openable video asset to test against.
            var exception = Record.Exception(() => player.TryPlay(host, invalidVideoPath));

            Assert.Null(exception);
        }
        finally
        {
            File.Delete(invalidVideoPath);
        }
    }

    [Fact]
    public void Dispose_IsSafeWhenTryPlayWasNeverCalled()
    {
        var player = new MediaFoundationVideoWallpaperPlayer();

        var exception = Record.Exception(player.Dispose);

        Assert.Null(exception);
    }

    [Fact]
    public void Dispose_IsSafeToCallTwice()
    {
        var player = new MediaFoundationVideoWallpaperPlayer();
        player.Dispose();

        var exception = Record.Exception(player.Dispose);

        Assert.Null(exception);
    }

    [RequiresDesktopSessionFact]
    public void Dispose_AfterAFailedTryPlay_IsSafe()
    {
        using var host = new Win32VideoWallpaperHost();
        Assert.True(host.TryAttach(), "TryAttach should succeed on a real interactive desktop session.");

        var player = new MediaFoundationVideoWallpaperPlayer();
        string nonExistentPath = Path.Combine(
            Path.GetTempPath(), $"cosmicwin-does-not-exist-{Guid.NewGuid():N}.mp4");
        player.TryPlay(host, nonExistentPath);

        var exception = Record.Exception(player.Dispose);

        Assert.Null(exception);
    }
}
