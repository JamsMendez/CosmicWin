using CosmicWin.Interop;
using CosmicWin.Interop.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;

namespace CosmicWin.Interop.Tests.Win32;

public sealed class MediaFoundationVideoWallpaperPlayerFrameOverlayTests
{
    [Fact]
    public void Tick_DrawsOverlayAfterVideoTransferAndBeforePresent()
    {
        var calls = new List<string>();
        var overlay = new RecordingFrameOverlay(calls);

        MediaFoundationVideoWallpaperPlayer.TickForTests(
            overlay,
            onVideoStreamTick: () => calls.Add("tick"),
            getBackBuffer: () => null!,
            getDesc: _ => new D3D11_TEXTURE2D_DESC { Width = 800, Height = 450 },
            transferVideoFrame: (_, destination) =>
            {
                calls.Add($"transfer:{destination.right}x{destination.bottom}");
            },
            present: () => calls.Add("present"));

        Assert.Equal(
            new[] { "tick", "transfer:800x450", "overlay:800x450", "present" },
            calls);
    }

    [Fact]
    public void Tick_WhenOverlayThrows_StillPresentsTransferredVideoFrame()
    {
        var calls = new List<string>();
        var overlay = new ThrowingFrameOverlay(calls);

        var exception = Record.Exception(() => MediaFoundationVideoWallpaperPlayer.TickForTests(
            overlay,
            onVideoStreamTick: () => calls.Add("tick"),
            getBackBuffer: () => null!,
            getDesc: _ => new D3D11_TEXTURE2D_DESC { Width = 1024, Height = 768 },
            transferVideoFrame: (_, destination) =>
            {
                calls.Add($"transfer:{destination.right}x{destination.bottom}");
            },
            present: () => calls.Add("present")));

        Assert.Null(exception);
        Assert.Equal(
            new[] { "tick", "transfer:1024x768", "overlay", "present" },
            calls);
    }

    [Fact]
    public void DefaultConstructor_UsesNoOpOverlay()
    {
        using var player = new MediaFoundationVideoWallpaperPlayer();

        Assert.NotNull(player);
    }

    private sealed class RecordingFrameOverlay(List<string> calls) : IFrameOverlay
    {
        public void Draw(ID3D11Texture2D backBuffer, RECT destination)
        {
            calls.Add($"overlay:{destination.right}x{destination.bottom}");
        }
    }

    private sealed class ThrowingFrameOverlay(List<string> calls) : IFrameOverlay
    {
        public void Draw(ID3D11Texture2D backBuffer, RECT destination)
        {
            calls.Add("overlay");
            throw new InvalidOperationException("overlay failed");
        }
    }
}
