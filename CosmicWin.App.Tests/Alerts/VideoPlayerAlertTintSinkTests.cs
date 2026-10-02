using System.Runtime.CompilerServices;
using CosmicWin.App.Alerts;
using CosmicWin.Interop.Win32;

namespace CosmicWin.App.Tests.Alerts;

/// <summary>
/// see-through-video-tint (S4): the tint sink exists ONLY in video wallpaper mode; html / mini modes
/// (no video back buffer) get none, so the controller never tells the page to tint there.
/// </summary>
public sealed class VideoPlayerAlertTintSinkTests
{
    [Theory]
    [InlineData(WallpaperMode.Html)]
    [InlineData(WallpaperMode.HtmlMini)]
    public void HtmlModesGetNoSink(WallpaperMode mode)
    {
        using var player = new MediaFoundationVideoWallpaperPlayer();
        Assert.Null(VideoPlayerAlertTintSink.For(mode, player));
    }

    [Fact]
    public void VideoModeWithoutAPlayerGetsNoSink()
    {
        Assert.Null(VideoPlayerAlertTintSink.For(WallpaperMode.Video, null));
    }

    [Fact]
    public void VideoModeGetsASinkThatForwardsToThePlayerWithoutThrowing()
    {
        using var player = new MediaFoundationVideoWallpaperPlayer();
        var sink = VideoPlayerAlertTintSink.For(WallpaperMode.Video, player);
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
        var sink = VideoPlayerAlertTintSink.For(WallpaperMode.Video, player)!;
        Action handler = () => { };

        sink.TintRendered += handler;
        sink.TintRendered -= handler;
        sink.TintLost += handler;
        sink.TintLost -= handler;
    }

    [Fact]
    public void AppCompositionWiresTheSinkOnlyThroughTheModeGate()
    {
        var source = ReadCompositionSource();
        Assert.Contains("tintSink: VideoPlayerAlertTintSink.For(settings.WallpaperMode, videoWallpaperPlayer)", source);
    }
}
