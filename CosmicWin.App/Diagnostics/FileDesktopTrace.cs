using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace CosmicWin.App.Diagnostics;

/// <summary>Records what a virtual-desktop chord actually did.</summary>
public interface IDesktopTrace
{
    void Record(string line);
}

/// <summary>
/// Appends one line per virtual-desktop chord, beside the focus trace.
/// </summary>
/// <remarks>
/// Written after the first live run of the desktop chords reported "Alt+N does nothing" with no way
/// to tell whether the chord arrived, the service was wired, the shell refused, or the switch was
/// silently reversed. The same lesson MR-2 taught: instrument before guessing, because a window
/// manager's failures are invisible by nature -- the user only sees that nothing moved.
/// <para>
/// Elevation is the specific unknown this exists to settle. Switching desktops works from an
/// ordinary test process; the app runs as administrator, and nothing measurable said whether that
/// is what breaks it.
/// </para>
/// </remarks>
public sealed class FileDesktopTrace(
    string path, Func<DateTimeOffset>? clock = null, TimeSpan? retryWindow = null) : IDesktopTrace
{
    /// <summary>
    /// A reader that opens the log with <see cref="FileShare.Read"/> (many editors, <c>Get-Content</c>
    /// without <c>-Wait</c>, <see cref="File.ReadAllLines(string)"/>) denies writers for as long as it
    /// holds the handle: 22-47 ms for <c>ReadAllLines</c> on the real 9 MB trace (measured
    /// 2026-09-25). Retrying for this long rides out that case with a 3x margin, without turning
    /// <see cref="Record"/> into a background queue or making it asynchronous. Bounded by elapsed
    /// TIME, not by a count of sleeps: a 5 ms sleep lasts ~15 ms at the default timer resolution but
    /// only 5 ms once Media Foundation (the video wallpaper) raises it to 1 ms, so a fixed count would
    /// shrink the window exactly while the wallpaper plays.
    /// </summary>
    private static readonly TimeSpan DefaultRetryWindow = TimeSpan.FromMilliseconds(150);

    private const int RetryDelayMilliseconds = 5;

    private const int ErrorSharingViolation = unchecked((int)0x80070020);
    private const int ErrorLockViolation = unchecked((int)0x80070021);

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly TimeSpan _retryWindow = retryWindow ?? DefaultRetryWindow;

    // Review R3-001: set when a line used up its whole retry window, cleared by the next write
    // that succeeds. While set, lines fail at once instead of waiting again, so a reader that holds
    // the file for a long time costs callers ONE window, not one per line -- Record runs on chord
    // and layout paths that write several lines in a row. Guarded by _gate.
    private bool _gaveUpOnLastHold;
    private readonly Lock _gate = new();

    public static string ResolveDefaultPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CosmicWin",
            "desktop-trace.log");

    /// <summary>
    /// Swallows every IO failure: the app under diagnosis must not crash because of its own
    /// diagnostics. A sharing violation -- another process holding the file open without write
    /// sharing -- is retried for a bounded time first, because that case is usually just a
    /// reader passing through; every other IO failure keeps today's behaviour of a single swallowed
    /// attempt.
    /// </summary>
    public void Record(string line)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var stamped = _clock().ToString("O", CultureInfo.InvariantCulture) + " " + line;
            lock (_gate)
            {
                var window = _gaveUpOnLastHold ? TimeSpan.Zero : _retryWindow;
                var elapsed = Stopwatch.StartNew();
                while (true)
                {
                    try
                    {
                        File.AppendAllText(path, stamped + Environment.NewLine);
                        _gaveUpOnLastHold = false;
                        return;
                    }
                    catch (IOException exception) when (IsSharingViolation(exception))
                    {
                        if (elapsed.Elapsed >= window)
                        {
                            _gaveUpOnLastHold = true;
                            throw;
                        }

                        Thread.Sleep(RetryDelayMilliseconds);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
        }
    }

    private static bool IsSharingViolation(IOException exception) =>
        exception.HResult == ErrorSharingViolation || exception.HResult == ErrorLockViolation;
}
