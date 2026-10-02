using System.Runtime.CompilerServices;
using CosmicWin.App.Alerts;
using CosmicWin.Interop.Win32;

namespace CosmicWin.App.Tests.Alerts;

/// <summary>
/// see-through-video-tint (S4): the tint sink exists ONLY when there is a video player to tint;
/// with none the controller never tells the page to tint.
/// </summary>
public sealed class VideoPlayerAlertTintSinkTests
{
    [Fact]
    public void NoPlayerGetsNoSink()
    {
        Assert.Null(VideoPlayerAlertTintSink.For(null));
    }

    [Fact]
    public void APlayerGetsASinkThatForwardsToThePlayerWithoutThrowing()
    {
        using var player = new MediaFoundationVideoWallpaperPlayer();
        var sink = VideoPlayerAlertTintSink.For(player);
        Assert.NotNull(sink);
        sink.SetTint(new byte[4], 2, 2, 1, 2, 3); // no worker running: just records the request
        sink.ClearTint();
        Assert.Throws<ArgumentException>(() => sink.SetTint(new byte[3], 2, 2, 1, 2, 3)); // player validation passes through
    }

    private static string ReadCompositionSource([CallerFilePath] string testFilePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!,
            "..", "..", "CosmicWin.App", "AppComposition.cs")));

    [Fact]
    public void AppCompositionWiresTheTintDiagnosticsToTheDesktopTrace()
    {
        var source = ReadCompositionSource();
        Assert.Contains("new MediaFoundationVideoWallpaperPlayer(onTintDiagnostic: desktopTrace.Record)", source);
    }

    [Fact]
    public void TheSinkForwardsTheRenderedAndLostEventsSubscriptionsToThePlayer()
    {
        using var player = new MediaFoundationVideoWallpaperPlayer();
        var sink = VideoPlayerAlertTintSink.For(player)!;
        Action handler = () => { };

        sink.TintRendered += handler;
        sink.TintRendered -= handler;
        sink.TintLost += handler;
        sink.TintLost -= handler;
    }

    [Fact]
    public void AppCompositionWiresTheSinkToTheVideoPlayer()
    {
        var source = ReadCompositionSource();
        Assert.Contains("tintSink: VideoPlayerAlertTintSink.For(videoWallpaperPlayer)", source);
    }
}
