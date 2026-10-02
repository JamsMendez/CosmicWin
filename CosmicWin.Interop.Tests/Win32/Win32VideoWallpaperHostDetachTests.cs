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
}
