using CosmicWin.Interop;
using CosmicWin.Interop.Win32;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// The watcher that sees what the workspace deliberately cannot.
/// </summary>
/// <remarks>
/// <para>
/// <c>Win32NativeWindowSource.IsTrackable</c> is <c>!hasOwner &amp;&amp; !isCloaked</c>, and it
/// gates the hook arm itself — so a window with an owner never reaches the workspace at all. A
/// modal dialog always has an owner. This watcher exists because widening that gate would push
/// every tooltip, dropdown, context menu and IME candidate list through the tiling pipeline.
/// </para>
/// <para>
/// It reports and decides NOTHING. Every style bit is carried through untouched for the layer above
/// to judge, because this assembly has no idea what a dialog is.
/// </para>
/// </remarks>
public sealed class Win32WindowShownWatcherTests
{
    private const uint SysMenu = 0x00080000u;

    [Fact]
    public void AShownWindowIsReported_WithItsStyleBitsIntact()
    {
        var source = new FakeNativeWindowSource();
        source.SeedExistingWindow(
            0x11, "Save changes?", Rectangle.FromSize(10, 20, 400, 200),
            className: "#32770", processName: "app", style: SysMenu, isOwned: true);

        using var watcher = new Win32WindowShownWatcher(source);
        var seen = new List<IWindow>();
        watcher.WindowShown += (_, e) => seen.Add(e.Window);
        watcher.Open();

        source.SimulateWindowShown(0x11);

        var window = Assert.Single(seen);
        Assert.Equal(new IntPtr(0x11), window.Handle);
        Assert.Equal(Rectangle.FromSize(10, 20, 400, 200), window.Bounds);
        Assert.Equal(SysMenu, window.Style);
        Assert.True(window.IsOwned);
        Assert.Equal("#32770", window.ClassName);
    }

    /// <summary>
    /// The watcher itself judges nothing. What a shown window MEANS is a question for the layer
    /// that knows what a dialog is, so anything the source delivers is passed on intact -- which is
    /// also why the App-side filter cannot lean on the hook's gate and repeats the check.
    /// </summary>
    [Fact]
    public void WhateverTheSourceDelivers_IsPassedOnWithoutJudgement()
    {
        var source = new FakeNativeWindowSource();
        source.SeedExistingWindow(0x22, "Editor", Rectangle.FromSize(0, 0, 800, 600));

        using var watcher = new Win32WindowShownWatcher(source);
        var seen = 0;
        watcher.WindowShown += (_, _) => seen++;
        watcher.Open();

        source.SimulateWindowShown(0x22);

        Assert.Equal(1, seen);
    }

    /// <summary>
    /// The two hooks partition the desktop instead of overlapping it, and this is the seam that
    /// says so. An unowned window belongs to the tiling path, which already has it; reporting it
    /// here as well would invite two answers about where the same window belongs.
    /// <para>
    /// It is also what keeps the second hook cheap. Everything past this gate reads the window in
    /// full -- rectangle, DWM frame, class, title and a process handle -- and the hook fires for
    /// every window shown anywhere on the desktop, so without it every menu that opens would cost
    /// an OpenProcess.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void OnlyOwnedWindowsAreWorthReporting(bool hasOwner, bool expected)
    {
        Assert.Equal(expected, Win32NativeWindowSource.IsShownWindowWorthReporting(hasOwner));
    }

    /// <summary>Nothing may be reported by both hooks, in either direction.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NoWindowIsEverReportedByBothHooks(bool hasOwner)
    {
        Assert.False(
            Win32NativeWindowSource.IsTrackable(hasOwner, isCloaked: false)
            && Win32NativeWindowSource.IsShownWindowWorthReporting(hasOwner));
    }

    /// <summary>
    /// A window can die between being shown and being asked about. The event carries a handle, not
    /// a window, so the read is where that shows up -- and there is nothing to report if it fails.
    /// </summary>
    [Fact]
    public void AWindowTheSourceCannotDescribe_IsNotReported()
    {
        var source = new FakeNativeWindowSource();

        using var watcher = new Win32WindowShownWatcher(source);
        var seen = 0;
        watcher.WindowShown += (_, _) => seen++;
        watcher.Open();

        source.SimulateWindowShown(0x33);

        Assert.Equal(0, seen);
    }

    /// <summary>Nothing is raised before <see cref="IWindowShownWatcher.Open"/>: the hook is not installed yet.</summary>
    [Fact]
    public void NothingIsReportedBeforeOpen()
    {
        var source = new FakeNativeWindowSource();
        source.SeedExistingWindow(0x44, "Editor", Rectangle.FromSize(0, 0, 800, 600));

        using var watcher = new Win32WindowShownWatcher(source);
        var seen = 0;
        watcher.WindowShown += (_, _) => seen++;

        source.SimulateWindowShown(0x44);

        Assert.Equal(0, seen);
    }

    [Fact]
    public void DisposingReleasesTheSubscription()
    {
        var source = new FakeNativeWindowSource();
        source.SeedExistingWindow(0x55, "Editor", Rectangle.FromSize(0, 0, 800, 600));

        var watcher = new Win32WindowShownWatcher(source);
        var seen = 0;
        watcher.WindowShown += (_, _) => seen++;
        watcher.Open();
        watcher.Dispose();

        source.SimulateWindowShown(0x55);

        Assert.Equal(0, seen);
    }
}
