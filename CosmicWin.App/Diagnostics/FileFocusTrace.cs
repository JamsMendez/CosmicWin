using System.Globalization;
using System.IO;

namespace CosmicWin.App.Diagnostics;

/// <summary>
/// Appends one line per focus chord to a plain text file the user reads after a supervised
/// run. Lives beside the other <c>%LOCALAPPDATA%\CosmicWin</c> artifacts (the design, the same
/// convention <see cref="CosmicWin.App.ExceptionListFile"/> follows).
/// </summary>
/// <param name="path">Destination file; its directory is created on first write.</param>
/// <param name="clock">Timestamp source, injected so the format is assertable.</param>
public sealed class FileFocusTrace(string path, Func<DateTimeOffset>? clock = null) : IFocusTrace
{
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly Lock _gate = new();

    /// <summary>Default on-disk location, beside <see cref="CosmicWin.App.ExceptionListFile.ResolvePath"/>'s file.</summary>
    public static string ResolveDefaultPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CosmicWin",
            "focus-trace.log");

    /// <summary>
    /// Writes <paramref name="entry"/> and swallows every IO failure: a missing directory, a locked
    /// file or a bad path must never surface as a crash in the app under diagnosis.
    /// </summary>
    public void Record(FocusTraceEntry entry)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            lock (_gate)
            {
                File.AppendAllText(path, Format(entry) + Environment.NewLine);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            // Deliberately swallowed -- see the method summary.
        }
    }

    private string Format(FocusTraceEntry entry) => string.Create(
        CultureInfo.InvariantCulture,
        $"{_clock():yyyy-MM-ddTHH:mm:ss.fffZ} focus {entry.Direction} foreground=0x{entry.ForegroundHandle:X} focused=0x{entry.FocusedHandle:X} target=0x{entry.TargetHandle:X} {entry.Outcome} activation={entry.Activation?.ToString() ?? "none"}");
}
