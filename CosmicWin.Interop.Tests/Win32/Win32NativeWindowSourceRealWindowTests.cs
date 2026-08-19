using CosmicWin.Interop;
using CosmicWin.Interop.Win32;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// Task 0.10: first real exercise of the CsWin32-backed <see cref="Win32NativeWindowSource"/>
/// against an actual desktop session and a genuinely external process's window — everything
/// before this batch (WU1/WU2) only exercised the tracking/geometry/reposition algorithms via
/// <see cref="FakeNativeWindowSource"/>. Tagged <c>RequiresDesktop</c> so CI-safe unit runs can
/// exclude it: <c>dotnet test --filter Category!=RequiresDesktop</c>. Run the desktop-requiring
/// suite explicitly with <c>dotnet test --filter Category=RequiresDesktop</c>.
/// </summary>
[Trait("Category", "RequiresDesktop")]
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

    [Fact]
    public void TryGetWindowInfo_ReturnsFalse_OnceTheRealWindowHasBeenClosed()
    {
        var notepad = SpawnedNotepadWindow.Spawn();
        var handle = notepad.Handle;
        var source = new Win32NativeWindowSource();

        notepad.Dispose();
        WaitUntilTrue(() => !source.TryGetWindowInfo(handle, out _), TimeSpan.FromSeconds(5));

        Assert.False(source.TryGetWindowInfo(handle, out _));
    }

    private static void WaitUntilTrue(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !condition())
        {
            Thread.Sleep(50);
        }
    }
}
