using System.Runtime.InteropServices;
using CosmicWin.Interop.Win32;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// The desktop-free half of <see cref="Win32VideoWallpaperHost.Detach"/>'s contract: it is a
/// harmless no-op whenever there is no host window to take down, which is what lets the tray's
/// "Quitar wallpaper de video" call it unconditionally. The real detach/re-attach round trip needs a
/// desktop session and lives in <see cref="Win32VideoWallpaperHostRealAttachTests"/>.
/// </summary>
public sealed class Win32VideoWallpaperHostDetachTests
{
    [Fact]
    public void Detach_BeforeAnyAttach_IsANoOpAndDoesNotThrow()
    {
        using var host = new Win32VideoWallpaperHost();

        var exception = Record.Exception(() =>
        {
            host.Detach();
            host.Detach();
        });

        Assert.Null(exception);
        Assert.Equal(0, host.Hwnd);
    }

    [Fact]
    public void Detach_AfterDispose_IsANoOpAndDoesNotThrow()
    {
        var host = new Win32VideoWallpaperHost();
        host.Dispose();

        var exception = Record.Exception(host.Detach);

        Assert.Null(exception);
    }

    /// <summary>
    /// Releasing the swapchain throws: the host window must still be destroyed (it would otherwise
    /// stay alive and visible on the desktop), and Dispose must not destroy it a second time.
    /// </summary>
    [Fact]
    public void Detach_WhenReleasingTheSwapChainThrows_StillDestroysTheWindowExactlyOnce()
    {
        var hwnd = CreatePlainWindow();
        try
        {
            var destroyed = new List<nint>();
            var host = new Win32VideoWallpaperHost
            {
                ReleaseSwapChainResourcesForTest = () => throw new InvalidOperationException("release failed"),
                DestroyWindowForTest = window =>
                {
                    destroyed.Add(window);
                    return DestroyWindow(window);
                },
            };
            host.AdoptHostWindowForTest(hwnd);

            var exception = Record.Exception(host.Detach);

            Assert.Null(exception);
            Assert.False(IsWindow(hwnd));
            Assert.True(host.IsDetached);

            host.ReleaseSwapChainResourcesForTest = null;
            host.Dispose();

            Assert.Equal([hwnd], destroyed);
        }
        finally
        {
            DestroyIfAlive(hwnd);
        }
    }

    /// <summary>
    /// The window survives Detach (DestroyWindow failed): it must not be marked detached, or Dispose
    /// would skip it and a later TryAttach would overwrite the handle and leak a live window.
    /// </summary>
    [Fact]
    public void Detach_WhenTheWindowSurvives_StaysAttachedSoDisposeDestroysIt()
    {
        var hwnd = CreatePlainWindow();
        try
        {
            var destroyCalls = 0;
            var host = new Win32VideoWallpaperHost
            {
                ReleaseSwapChainResourcesForTest = () => throw new InvalidOperationException("release failed"),
                DestroyWindowForTest = window => ++destroyCalls > 1 && DestroyWindow(window),
            };
            host.AdoptHostWindowForTest(hwnd);

            host.Detach();

            Assert.True(IsWindow(hwnd));
            Assert.False(host.IsDetached);

            host.ReleaseSwapChainResourcesForTest = null;
            host.Dispose();

            Assert.False(IsWindow(hwnd));
            Assert.Equal(2, destroyCalls);
        }
        finally
        {
            DestroyIfAlive(hwnd);
        }
    }

    /// <summary>
    /// A removal is a removal even when DestroyWindow fails: an Explorer restart (TaskbarCreated)
    /// arriving afterwards must not re-attach the surviving window and bring the video back.
    /// </summary>
    [Fact]
    public void TaskbarCreated_AfterADetachWhoseDestroyFailed_DoesNotReattach()
    {
        var hwnd = CreatePlainWindow();
        try
        {
            var attachCalls = new List<nint>();
            var destroyCalls = 0;
            var host = new Win32VideoWallpaperHost
            {
                ReleaseSwapChainResourcesForTest = () => { },
                DestroyWindowForTest = window => ++destroyCalls > 1 && DestroyWindow(window),
                AttachToDesktopForTest = window =>
                {
                    attachCalls.Add(window);
                    return false;
                },
            };
            host.AdoptHostWindowForTest(hwnd);

            host.Detach();
            Assert.True(IsWindow(hwnd));

            host.RaiseTaskbarCreatedForTest();

            Assert.Empty(attachCalls);
            Assert.Equal(hwnd, host.Hwnd);

            host.Dispose();
            Assert.False(IsWindow(hwnd));
            Assert.Equal(2, destroyCalls);
        }
        finally
        {
            DestroyIfAlive(hwnd);
        }
    }

    /// <summary>
    /// A new pick after a removal whose destroy failed reuses the surviving window (no second window
    /// is created over it, so nothing leaks), clears the removal so Explorer restarts re-attach it
    /// again, and Dispose still destroys that one window exactly once.
    /// </summary>
    [Fact]
    public void TryAttach_AfterADetachWhoseDestroyFailed_ReusesTheSurvivingWindowAndDisposeDestroysItOnce()
    {
        var hwnd = CreatePlainWindow();
        try
        {
            var destroyed = new List<nint>();
            var attachCalls = new List<nint>();
            var failNextDestroy = true;
            var host = new Win32VideoWallpaperHost
            {
                ReleaseSwapChainResourcesForTest = () => { },
                DestroyWindowForTest = window =>
                {
                    destroyed.Add(window);
                    if (failNextDestroy)
                    {
                        failNextDestroy = false;
                        return false;
                    }

                    return DestroyWindow(window);
                },
                AttachToDesktopForTest = window =>
                {
                    attachCalls.Add(window);
                    return false; // Stops before D3D: no swapchain on a test window.
                },
            };
            host.AdoptHostWindowForTest(hwnd);

            host.Detach();
            Assert.True(IsWindow(hwnd));

            host.TryAttach();

            Assert.Equal([hwnd], attachCalls);
            Assert.Equal(hwnd, host.Hwnd);

            // The pick cleared the removal: an Explorer restart re-attaches the shown host again.
            host.RaiseTaskbarCreatedForTest();
            Assert.Equal([hwnd, hwnd], attachCalls);

            host.ReleaseSwapChainResourcesForTest = null;
            host.Dispose();

            Assert.False(IsWindow(hwnd));
            Assert.Equal([hwnd, hwnd], destroyed);
        }
        finally
        {
            DestroyIfAlive(hwnd);
        }
    }

    /// <summary>
    /// The real registered <c>TaskbarCreated</c> message, sent straight to the hidden receiver (never
    /// broadcast), reaches the window procedure and re-attaches a shown host; after a removal the
    /// same message leaves it alone. Proves the wndproc routing the RaiseTaskbarCreatedForTest seam
    /// skips. The receiver is a hidden, never-shown popup, so no desktop session is needed.
    /// </summary>
    [Fact]
    public void TaskbarCreatedMessage_SentToTheReceiver_ReattachesAShownHostButNotARemovedOne()
    {
        var hwnd = CreatePlainWindow();
        try
        {
            var attachCalls = new List<nint>();
            var host = new Win32VideoWallpaperHost
            {
                ReleaseSwapChainResourcesForTest = () => { },
                AttachToDesktopForTest = window =>
                {
                    attachCalls.Add(window);
                    return false; // Stops before D3D: no swapchain on a test window.
                },
            };
            host.AdoptHostWindowForTest(hwnd);

            host.TryAttach(); // Creates the real receiver (real class, real WndProc).
            var receiver = host.TaskbarMessageHwnd;
            Assert.NotEqual(0, receiver);
            Assert.Equal([hwnd], attachCalls);

            var taskbarCreated = RegisterWindowMessage("TaskbarCreated");
            Assert.NotEqual(0u, taskbarCreated);

            SendMessage(receiver, taskbarCreated, 0, 0);
            Assert.Equal([hwnd, hwnd], attachCalls);

            host.Detach();
            SendMessage(receiver, taskbarCreated, 0, 0);
            Assert.Equal([hwnd, hwnd], attachCalls);

            host.ReleaseSwapChainResourcesForTest = null;
            host.Dispose();
            Assert.False(IsWindow(receiver));
        }
        finally
        {
            DestroyIfAlive(hwnd);
        }
    }

    /// <summary>A second Detach after a failed destroy retries it, and Dispose then leaves the window alone.</summary>
    [Fact]
    public void Detach_AfterADetachWhoseDestroyFailed_RetriesTheDestroy()
    {
        var hwnd = CreatePlainWindow();
        try
        {
            var destroyed = new List<nint>();
            var host = new Win32VideoWallpaperHost
            {
                ReleaseSwapChainResourcesForTest = () => { },
                DestroyWindowForTest = window =>
                {
                    destroyed.Add(window);
                    return destroyed.Count > 1 && DestroyWindow(window);
                },
            };
            host.AdoptHostWindowForTest(hwnd);

            host.Detach();
            Assert.True(IsWindow(hwnd));
            Assert.False(host.IsDetached);

            host.Detach();

            Assert.False(IsWindow(hwnd));
            Assert.True(host.IsDetached);

            host.ReleaseSwapChainResourcesForTest = null;
            host.Dispose();

            Assert.Equal([hwnd, hwnd], destroyed);
        }
        finally
        {
            DestroyIfAlive(hwnd);
        }
    }

    /// <summary>A fatal exception from the release is not swallowed, but the window still goes.</summary>
    [Fact]
    public void Detach_WhenReleasingThrowsAFatalException_PropagatesItButStillDestroysTheWindow()
    {
        var hwnd = CreatePlainWindow();
        try
        {
            var host = new Win32VideoWallpaperHost
            {
                ReleaseSwapChainResourcesForTest = () => throw new OutOfMemoryException(),
            };
            host.AdoptHostWindowForTest(hwnd);

            Assert.Throws<OutOfMemoryException>(host.Detach);

            Assert.False(IsWindow(hwnd));
            host.ReleaseSwapChainResourcesForTest = null;
            host.Dispose();
        }
        finally
        {
            DestroyIfAlive(hwnd);
        }
    }

    // A hidden, unparented STATIC window owned by the test thread: real enough for IsWindow and
    // DestroyWindow, and needs no interactive desktop (it is never shown or attached).
    private static nint CreatePlainWindow()
    {
        var hwnd = CreateWindowEx(0, "STATIC", "cosmicwin-detach-test", 0, 0, 0, 1, 1, 0, 0, 0, 0);
        Assert.NotEqual(0, hwnd);
        return hwnd;
    }

    private static void DestroyIfAlive(nint hwnd)
    {
        if (IsWindow(hwnd))
        {
            DestroyWindow(hwnd);
        }
    }

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hwnd);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hwnd);
}
