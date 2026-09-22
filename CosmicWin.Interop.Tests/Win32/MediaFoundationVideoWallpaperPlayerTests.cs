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

    /// <summary>Mirrors <see cref="Dispose_IsSafeWhenTryPlayWasNeverCalled"/> for the new <c>Stop</c>
    /// member: a re-pick that arrives before anything has ever played must not throw.</summary>
    [Fact]
    public void Stop_IsSafeWhenTryPlayWasNeverCalled()
    {
        var player = new MediaFoundationVideoWallpaperPlayer();

        var exception = Record.Exception(player.Stop);

        Assert.Null(exception);
    }

    /// <summary>Stop is documented never to throw, including after the player has already been
    /// retired -- the interface contract a caller reaching Stop from a background thread relies on
    /// without checking disposal state itself first.</summary>
    [Fact]
    public void Stop_AfterDispose_IsSafe()
    {
        var player = new MediaFoundationVideoWallpaperPlayer();
        player.Dispose();

        var exception = Record.Exception(player.Stop);

        Assert.Null(exception);
    }

    /// <summary>
    /// F3 (video-wallpaper-review-followups, <c>R3-stop-release-unproved</c>): the one gap the
    /// class remarks above name honestly -- until now, nothing here ever played a REAL video, so
    /// nothing proved <see cref="MediaFoundationVideoWallpaperPlayer.Stop"/> actually releases the
    /// file it was playing (the very property the T1 fix in
    /// <c>odd/tasks/video-wallpaper-repick-and-slideshow.md</c> depends on: the re-pick closure in
    /// <c>AppComposition</c> calls <c>Stop()</c> and then immediately overwrites that same file).
    /// Plays a tiny, real, checked-in H.264 MP4 (<c>Fixtures/tiny-h264.mp4</c>), stops, then opens
    /// the SAME file for exclusive read/write -- which only succeeds if no handle is still held.
    /// </summary>
    /// <remarks>
    /// Copied to a fresh temp path first so the exclusive open never touches the checked-in fixture
    /// file itself, and so a failed run cannot leave that fixture locked or dirtied for the next one.
    /// </remarks>
    [RequiresDesktopSessionFact]
    public void TryPlay_ThenStop_ReleasesTheFileForExclusiveAccess()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "tiny-h264.mp4");
        Assert.True(File.Exists(fixturePath), $"Fixture not found at {fixturePath}.");

        var playedPath = Path.Combine(Path.GetTempPath(), $"cosmicwin-stop-release-{Guid.NewGuid():N}.mp4");
        File.Copy(fixturePath, playedPath);

        try
        {
            using var host = new Win32VideoWallpaperHost();
            Assert.True(host.TryAttach(), "TryAttach should succeed on a real interactive desktop session.");

            using var player = new MediaFoundationVideoWallpaperPlayer();

            Assert.True(player.TryPlay(host, playedPath), "TryPlay should succeed against a real, valid MP4.");

            // Gives the engine's async source resolution time to actually open the file before
            // Stop() is asked to release it -- SetSource is documented asynchronous, and calling
            // Stop() in the same instant TryPlay returns could race ahead of the open itself.
            Thread.Sleep(300);

            player.Stop();

            // Exclusive: FileShare.None. This throws IOException (sharing violation) if Media
            // Foundation's worker thread still holds any handle open on the file.
            var exception = Record.Exception(() =>
            {
                using var exclusive = new FileStream(
                    playedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            });

            Assert.Null(exception);
        }
        finally
        {
            File.Delete(playedPath);
        }
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
