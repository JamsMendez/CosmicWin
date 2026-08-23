using CosmicWin.Interop.Win32;
using Xunit.Abstractions;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// TEMPORARY diagnostic, not a behavioural test. Reported: tiled windows at the screen
/// edge look inset on the left, right and bottom but flush against the top. Win32's
/// <c>GetWindowRect</c> includes a window's INVISIBLE resize border, which on this Windows build is
/// present on three sides and absent on the fourth; <c>DWMWA_EXTENDED_FRAME_BOUNDS</c> reports what
/// is actually drawn. Prints both for a real spawned window so the asymmetry can be measured rather
/// than assumed. Read-only: positions one window it spawned itself and never touches anything else.
/// </summary>
public sealed class FrameBoundsDiagnostic(ITestOutputHelper output)
{
    [RequiresDesktopFact]
    public void ReportWindowRectVersusDrawnFrame()
    {
        using var spawned = SpawnedAlacrittyWindow.Spawn();
        var source = new Win32NativeWindowSource();

        var requested = Rectangle.FromSize(200, 200, 900, 600);
        Assert.True(source.SetWindowPosition(spawned.Handle, requested));
        Thread.Sleep(600);

        Assert.True(source.TryGetWindowInfo(spawned.Handle, out var info));
        var windowRect = info.Bounds;
        Win32NativeWindowSource.TryGetDrawnFrameBounds(spawned.Handle, out var frame);

        output.WriteLine($"requested      : L={requested.Left} T={requested.Top} R={requested.Right} B={requested.Bottom}");
        output.WriteLine($"GetWindowRect  : L={windowRect.Left} T={windowRect.Top} R={windowRect.Right} B={windowRect.Bottom}");
        output.WriteLine($"drawn frame    : L={frame.Left} T={frame.Top} R={frame.Right} B={frame.Bottom}");
        output.WriteLine(
            "invisible inset: " +
            $"left={frame.Left - windowRect.Left} " +
            $"top={frame.Top - windowRect.Top} " +
            $"right={windowRect.Right - frame.Right} " +
            $"bottom={windowRect.Bottom - frame.Bottom}");    }
}
