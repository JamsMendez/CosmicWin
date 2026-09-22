using CosmicWin.Interop.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// T3: first real exercise of <see cref="Win32VideoWallpaperHost"/> against an actual desktop
/// session and a real GPU. Attaches for real and asserts on observable Win32 state, mirroring the
/// diagnostics <c>spikes/AttachSpike/Program.cs</c> already printed when this sequence was first
/// proven -- <c>GetParent</c>, <c>IsWindowVisible</c>, <c>GWL_STYLE</c> -- plus that the new host
/// window is excluded from <see cref="Win32NativeWindowSource"/>'s own tiling enumeration, which
/// confirms the <c>WS_CHILD</c> exclusion the task's constraint relies on rather than assuming it.
/// </summary>
/// <remarks>
/// <see cref="RequiresDesktopSessionFactAttribute"/>, not <see cref="RequiresDesktopFactAttribute"/>:
/// this fact spawns no terminal (see <see cref="DesktopGate"/>'s own documented distinction --
/// demanding a terminal binary of a fact that never touches one would delete coverage on machines
/// that lack it for no reason). It needs only the desktop opt-in and no live window manager to tile
/// the window it spawns away.
/// </remarks>
[Trait("Category", "RequiresDesktop")]
[Collection(RealDesktopCollection.Name)]
public sealed unsafe class Win32VideoWallpaperHostRealAttachTests
{
    private const uint WsChild = 0x40000000;

    [RequiresDesktopSessionFact]
    public void TryAttach_AttachesTheHostWindowBehindTheDesktopIcons()
    {
        using var host = new Win32VideoWallpaperHost();

        var attached = host.TryAttach();

        Assert.True(attached, "TryAttach should succeed on a real interactive desktop session.");
        Assert.NotEqual(0, host.Hwnd);
        Assert.NotEqual(0, host.TaskbarMessageHwnd);

        HWND taskbarReceiver = new(host.TaskbarMessageHwnd);
        Assert.Equal(HWND.Null, PInvoke.GetParent(taskbarReceiver));
        Assert.False(PInvoke.IsWindowVisible(taskbarReceiver).Value != 0);

        HWND hwnd = new(host.Hwnd);
        Assert.Equal(ResolveExpectedDesktopParent(), PInvoke.GetParent(hwnd));
        Assert.True(PInvoke.IsWindowVisible(hwnd).Value != 0);

        var style = unchecked((uint)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE));
        Assert.Equal(WsChild, style & WsChild);

        // The tiler's own admission path must exclude it automatically -- confirms rather than
        // assumes the constraint in odd/tasks/video-wallpaper.md ("the tiler's existing
        // IsTrackable check excludes it with zero extra work").
        var source = new Win32NativeWindowSource();
        Assert.DoesNotContain(host.Hwnd, source.EnumerateTopLevelWindows());
    }

    private static HWND ResolveExpectedDesktopParent()
    {
        HWND progman = PInvoke.FindWindow(null, "Program Manager");
        Assert.NotEqual(HWND.Null, progman);

        var exStyle = unchecked((uint)PInvoke.GetWindowLong(progman, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE));
        if (DesktopLayoutDetector.IsRaisedDesktop(exStyle))
        {
            return progman;
        }

        HWND ownerOfDefView = FindTopLevelOwningDefView();
        Assert.NotEqual(HWND.Null, ownerOfDefView);

        HWND workerW = PInvoke.FindWindowEx(HWND.Null, ownerOfDefView, "WorkerW", null);
        Assert.NotEqual(HWND.Null, workerW);
        return workerW;
    }

    private static HWND FindTopLevelOwningDefView()
    {
        HWND found = HWND.Null;
        PInvoke.EnumWindows(
            (hwnd, _) =>
            {
                HWND defView = PInvoke.FindWindowEx(hwnd, HWND.Null, "SHELLDLL_DefView", null);
                if (!defView.IsNull)
                {
                    found = hwnd;
                    return false;
                }

                return true;
            },
            IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// The <c>TaskbarCreated</c> re-attach path never recreates the D3D11 device/swapchain --
    /// only the second half of <c>TryAttach</c> (parent/z-order) reruns.
    /// </summary>
    [RequiresDesktopSessionFact]
    public void TryAttach_CalledASecondTime_ReattachesWithoutRecreatingTheDevice()
    {
        using var host = new Win32VideoWallpaperHost();

        Assert.True(host.TryAttach());
        var firstBackBuffer = host.GetBackBuffer();
        var firstHwnd = host.Hwnd;

        Assert.True(host.TryAttach());

        Assert.Equal(firstHwnd, host.Hwnd);
        Assert.Same(firstBackBuffer, host.GetBackBuffer());
    }

    /// <summary>
    /// T3 (video-wallpaper-repick-and-slideshow): T2 proved live on this same raised-desktop
    /// layout that the Windows wallpaper slideshow inserts a NEW wallpaper WorkerW directly after
    /// <c>SHELLDLL_DefView</c> -- i.e. directly ABOVE the host -- every time it changes image, and
    /// nothing re-raised it. This spawns its OWN window in that exact position, standing in for
    /// Explorer's new wallpaper layer without waiting on a real slideshow tick, then asserts
    /// <see cref="Win32VideoWallpaperHost.TryAttach"/> puts the host back directly after DefView.
    /// </summary>
    /// <remarks>
    /// F4 (video-wallpaper-review-followups, <c>R3-realattach-assumes-raised-layout</c>): the
    /// <c>Assert.NotEqual(HWND.Null, defView)</c> below assumes DefView sits directly under
    /// <see cref="ResolveExpectedDesktopParent"/>'s answer, which only holds on the raised-desktop
    /// layout this machine happens to run -- on the legacy WorkerW layout DefView lives under a
    /// DIFFERENT top-level window than the one the host attaches to (see that method's own two
    /// branches), so the assumption does not apply there. <see
    /// cref="RequiresRaisedDesktopLayoutFactAttribute"/> SKIPS this fact on that layout instead of
    /// reporting a failure that is not actually a defect.
    /// </remarks>
    [RequiresRaisedDesktopLayoutFact]
    public void TryAttach_WhenAWindowIsInsertedDirectlyAfterDefView_ReRaisesTheHostAboveIt()
    {
        using var host = new Win32VideoWallpaperHost();
        Assert.True(host.TryAttach());

        HWND hostParent = ResolveExpectedDesktopParent();
        HWND defView = PInvoke.FindWindowEx(hostParent, HWND.Null, "SHELLDLL_DefView", null);
        Assert.NotEqual(HWND.Null, defView);

        HWND hwnd = new(host.Hwnd);

        HWND simulatedSlideshowLayer = HWND.Null;
        try
        {
            simulatedSlideshowLayer = PInvoke.CreateWindowEx(
                0,
                "Static",
                "T3 simulated slideshow WorkerW",
                WINDOW_STYLE.WS_CHILD,
                0, 0, 1, 1,
                hostParent,
                null,
                PInvoke.GetModuleHandle((string?)null),
                null);
            Assert.NotEqual(HWND.Null, simulatedSlideshowLayer);

            Assert.True(PInvoke.SetWindowPos(
                simulatedSlideshowLayer,
                defView,
                0, 0, 0, 0,
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOMOVE
                | SET_WINDOW_POS_FLAGS.SWP_NOSIZE));

            // The host is knocked out of place: something else now sits directly after DefView.
            Assert.NotEqual(hwnd, PInvoke.GetWindow(defView, GET_WINDOW_CMD.GW_HWNDNEXT));

            Assert.True(host.TryAttach());

            // Re-raised: the host is directly after DefView again.
            Assert.Equal(hwnd, PInvoke.GetWindow(defView, GET_WINDOW_CMD.GW_HWNDNEXT));
        }
        finally
        {
            if (simulatedSlideshowLayer != HWND.Null)
            {
                PInvoke.DestroyWindow(simulatedSlideshowLayer);
            }
        }
    }
}
