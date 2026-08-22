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
public sealed class FileDesktopTrace(string path, Func<DateTimeOffset>? clock = null) : IDesktopTrace
{
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly Lock _gate = new();

    public static string ResolveDefaultPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CosmicWin",
            "desktop-trace.log");

    /// <summary>Swallows every IO failure: the app under diagnosis must not crash because of its own diagnostics.</summary>
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
                File.AppendAllText(path, stamped + Environment.NewLine);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
        }
    }
}
