using CosmicWin.Interop;
using CosmicWin.Interop.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// Task 0.10: first real exercise of the CsWin32-backed <see cref="Win32NativeWindowSource"/>
/// against an actual desktop session and a genuinely external process's window — everything
/// before this batch only exercised the tracking/geometry/reposition algorithms via
/// <see cref="FakeNativeWindowSource"/>. Tagged <c>RequiresDesktop</c> so CI-safe unit runs can
/// exclude it: <c>dotnet test --filter Category!=RequiresDesktop</c>. Run the desktop-requiring
/// suite explicitly with <c>dotnet test --filter Category=RequiresDesktop</c>.
/// </summary>
[Trait("Category", "RequiresDesktop")]
[Collection(RealDesktopCollection.Name)]
public sealed class Win32NativeWindowSourceRealWindowTests
{
    [Fact]
    public void EnumerateTopLevelWindows_IncludesASpawnedRealWindow()
    {
        using var notepad = SpawnedNotepadWindow.Spawn();
        var source = new Win32NativeWindowSource();

        var handles = source.EnumerateTopLevelWindows();

        Assert.Contains(notepad.Handle, handles);
    }

    [Fact]
    public void TryGetWindowInfo_ReturnsRealTitleAndNonEmptyBounds_ForASpawnedWindow()
    {
        using var notepad = SpawnedNotepadWindow.Spawn();
        var source = new Win32NativeWindowSource();

        var found = source.TryGetWindowInfo(notepad.Handle, out var info);

        Assert.True(found);
        Assert.Contains("Notepad", info.Title, StringComparison.OrdinalIgnoreCase);
        Assert.True(info.Bounds.Width > 0);
        Assert.True(info.Bounds.Height > 0);
    }

    [Fact]
    public void SetWindowPosition_MovesTheRealWindow_AndSubsequentReadReflectsExactNewBounds()
    {
        using var notepad = SpawnedNotepadWindow.Spawn();
        var source = new Win32NativeWindowSource();
        var target = Rectangle.FromSize(left: 50, top: 60, width: 500, height: 400);

        var moved = source.SetWindowPosition(notepad.Handle, target);

        Assert.True(moved);
        Assert.True(source.TryGetWindowInfo(notepad.Handle, out var info));
        Assert.Equal(target, info.Bounds);
    }

    /// <summary>
    /// <c>ReadStyle</c>/<c>ReadClassName</c>/<c>ReadProcessName</c>/<c>ReadIsOwned</c> had
    /// zero automated coverage -- four mutations to those P/Invoke reads (including <c>ReadStyle</c>
    /// returning <c>0</c>, which would make every real window fail the <c>WS_SYSMENU</c> check and
    /// silently disable tiling entirely) survived the full suite. Uses the same spawned, self-owned
    /// window as the other facts in this file -- never asserts against ambient desktop state.
    /// </summary>
    [Fact]
    public void TryGetWindowInfo_ReturnsRealDescriptorFields_ForASpawnedWindow()
    {
        const uint WsSysMenu = 0x00080000;

        using var notepad = SpawnedNotepadWindow.Spawn();
        var source = new Win32NativeWindowSource();

        var found = source.TryGetWindowInfo(notepad.Handle, out var info);

        Assert.True(found);
        Assert.False(string.IsNullOrWhiteSpace(info.ClassName));
        Assert.False(string.IsNullOrWhiteSpace(info.ProcessName));
        Assert.Contains("notepad", info.ProcessName, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(0u, info.Style);
        Assert.Equal(WsSysMenu, info.Style & WsSysMenu);
        Assert.False(info.IsOwned);
    }

    /// <summary>
    /// This test was load-flaky (3/3 passed idle, 2/2 failed under back-to-back suite
    /// runs) because <see cref="SpawnedNotepadWindow.Dispose"/> only requested a close and could
    /// return before the process had actually exited under load. <see
    /// cref="SpawnedNotepadWindow.Dispose"/> now escalates to a forced kill if the graceful close
    /// does not finish within its own bound, so by the time <c>Dispose()</c> returns here the
    /// process is guaranteed gone -- the poll below only has to wait out the OS's own window-handle
    /// teardown lag, not the target process's message-pump latency, so its bound is widened from 5s
    /// to 10s for headroom rather than to paper over an indeterminate wait. What this still proves,
    /// unchanged: the production <see cref="Win32NativeWindowSource.TryGetWindowInfo"/> genuinely
    /// stops resolving a handle once its real window is gone -- no shortcut, mock, or reduced
    /// assertion was introduced.
    /// </summary>
    [Fact]
    public void TryGetWindowInfo_ReturnsFalse_OnceTheRealWindowHasBeenClosed()
    {
        var notepad = SpawnedNotepadWindow.Spawn();
        var handle = notepad.Handle;
        var source = new Win32NativeWindowSource();

        notepad.Dispose();
        WaitUntilTrue(() => !source.TryGetWindowInfo(handle, out _), TimeSpan.FromSeconds(10));

        Assert.False(source.TryGetWindowInfo(handle, out _));
    }

    /// <summary>
    /// MR-1 : a real desktop enumeration found this window is a persistent phantom
    /// (unowned, cloaked, owned by explorer.exe itself) that used to pass every check and take a
    /// tiling slot. Confirms the fix on the exact real class this was measured against, without
    /// asserting anything about ambient desktop state beyond this one handle.
    /// </summary>
    [Fact]
    public void EnumerateTopLevelWindows_ExcludesTheRealExplorerApplicationFrameWindowPhantom()
    {
        var source = new Win32NativeWindowSource();

        var handles = source.EnumerateTopLevelWindows();

        foreach (var handle in handles)
        {
            Assert.True(
                source.TryGetWindowInfo(handle, out var info) && info.ClassName != "ApplicationFrameWindow",
                "A cloaked ApplicationFrameWindow phantom should never reach EnumerateTopLevelWindows.");
        }
    }

    /// <summary>
    /// MR-2 ( first real run): observation #96 measured plain <c>SetForegroundWindow</c>
    /// returning <c>false</c> from a background process on this exact machine. This proves the
    /// <c>AttachThreadInput</c> fix actually moves the real OS foreground -- not just that the call
    /// returns a boolean -- against a genuinely external spawned window.
    /// </summary>
    [Fact]
    public void TryActivateWindow_MovesTheRealForeground_EvenFromABackgroundProcess()
    {
        using var notepad = SpawnedNotepadWindow.Spawn();
        var source = new Win32NativeWindowSource();

        var activated = source.TryActivateWindow(notepad.Handle);

        Assert.True(activated);
        WaitUntilTrue(() => PInvoke.GetForegroundWindow() == new HWND(notepad.Handle), TimeSpan.FromSeconds(5));
        Assert.Equal(new HWND(notepad.Handle), PInvoke.GetForegroundWindow());
    }

    private static void WaitUntilTrue(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !condition())
        {
            Thread.Sleep(50);
        }
    }

    /// <summary>
    /// Reported: tiled windows looked inset on the left, right and bottom but flush
    /// against the top. Measured cause: Win32's <c>GetWindowRect</c> includes the INVISIBLE resize
    /// border, which on this build is 7px on three sides and 0 on the top --
    /// <c>invisible inset: left=7 top=0 right=7 bottom=7</c>. Positioning on those coordinates
    /// means the window the user actually SEES never lands where it was asked to.
    /// <para>
    /// Both halves of this seam now speak the DRAWN frame: ask for a rectangle and the drawn frame
    /// is that rectangle, read it back and you get the same numbers. They have to agree, or
    /// <c>Win32Workspace.UpdateBounds</c> would see a permanent 7px discrepancy between the cached
    /// value and the live one and re-report a bounds change forever.
    /// </para>
    /// </summary>
    [RequiresDesktopFact]
    public void SetWindowPosition_PlacesTheDRAWNFrameExactlyWhereAsked_AndReadsBackTheSame()
    {
        using var spawned = SpawnedAlacrittyWindow.Spawn();
        var source = new Win32NativeWindowSource();
        var requested = Rectangle.FromSize(300, 250, 800, 500);

        Assert.True(source.SetWindowPosition(spawned.Handle, requested));
        Thread.Sleep(600);

        Assert.True(Win32NativeWindowSource.TryGetDrawnFrameBounds(spawned.Handle, out var drawn));
        Assert.Equal(requested, drawn);

        Assert.True(source.TryGetWindowInfo(spawned.Handle, out var info));
        Assert.Equal(requested, info.Bounds);
    }
}
