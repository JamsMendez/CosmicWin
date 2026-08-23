using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;

namespace CosmicWin.App.Tests;

/// <summary>Pins the literal Interop-&gt;Layout adapter <see cref="WindowDescriptorBuilder"/> -- the only place an <c>IWindow</c> can become a <c>WindowDescriptor</c>.</summary>
public sealed class WindowDescriptorBuilderTests
{
    [Fact]
    public void Build_MapsAllFiveWindowLevelFields_FromIWindow_ToWindowDescriptor()
    {
        var window = new RecordingWindow(
            new IntPtr(1),
            Rectangle.FromSize(0, 0, 800, 600),
            className: "Shell_TrayWnd",
            processName: "explorer.exe",
            style: 0x00080000u,
            exStyle: 0x00000080u,
            isOwned: true);

        var descriptor = WindowDescriptorBuilder.Build(window);

        Assert.Equal("Shell_TrayWnd", descriptor.ClassName);
        Assert.Equal("explorer.exe", descriptor.ProcessName);
        Assert.Equal("Recording", descriptor.Title);
        Assert.Equal(0x00000080u, descriptor.ExStyle);
        Assert.Equal(0x00080000u, descriptor.Style);
        Assert.True(descriptor.IsOwned);
    }
}
