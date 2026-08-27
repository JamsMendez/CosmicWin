using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;

namespace CosmicWin.App.Tests;

/// <summary>Pins the literal Interop-&gt;Layout adapter <see cref="WindowDescriptorBuilder"/> -- the only place an <c>IWindow</c> can become a <c>WindowDescriptor</c>.</summary>
public sealed class WindowDescriptorBuilderTests
{
    [Fact]
    public void Build_MapsEveryWindowLevelField_FromIWindow_ToWindowDescriptor()
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

        // The zero-area exclusion reads these, so a builder that reported a made-up size would
        // leave that rule pinned and inert. Caught by mutation: hard-coding 1x1 here reddened
        // nothing at all until this line existed.
        Assert.Equal(800, descriptor.Width);
        Assert.Equal(600, descriptor.Height);
    }

    /// <summary>
    /// The exact shape the rule exists for: Windows 11's InputNonClientPointerSource, which is
    /// visible, unowned and styled like an ordinary window, and measures nothing.
    /// </summary>
    [Fact]
    public void Build_ZeroSizedWindow_CarriesThatSizeThrough()
    {
        var plumbing = new RecordingWindow(
            new IntPtr(2),
            Rectangle.FromSize(2583, 8, 0, 0),
            className: "InputNonClientPointerSource",
            processName: "Notepad.exe");

        var descriptor = WindowDescriptorBuilder.Build(plumbing);

        Assert.Equal(0, descriptor.Width);
        Assert.Equal(0, descriptor.Height);
        Assert.True(CosmicWin.Layout.Filters.WindowFilters.IsAutoExcluded(descriptor));
    }
}
