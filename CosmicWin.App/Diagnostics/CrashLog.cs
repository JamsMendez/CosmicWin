using System.Globalization;
using System.IO;

namespace CosmicWin.App.Diagnostics;

/// <summary>
/// Writes every unhandled exception, with its full stack, to <c>crash.log</c> beside the traces.
/// </summary>
/// <remarks>
/// Written after a crash on 2026-10-28 left nothing behind but a WER bucket for <c>0xe0434352</c>
/// (an unhandled managed exception): no <c>.NET Runtime</c> event, no kept minidump, no stack. The
/// user only saw that windows stopped being tiled, and nothing said where it broke.
/// <para>
/// It RECORDS and nothing else. The process still dies exactly as it did before: swallowing the
/// exception would leave a window manager running in a state nobody reasoned about.
/// </para>
/// </remarks>
public static class CrashLog
{
    public static string ResolveDefaultPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CosmicWin",
            "crash.log");

    /// <summary>
    /// Subscribes to <see cref="AppDomain.UnhandledException"/>, which every thread reaches -- the
    /// WPF UI thread included -- before the runtime tears the process down.
    /// </summary>
    public static void Install(string path) =>
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Record(path, e.ExceptionObject, e.IsTerminating, DateTimeOffset.UtcNow);

    /// <summary>
    /// Appends one entry. Swallows every IO failure: a crash handler that throws replaces the
    /// exception worth reading with one about the log file.
    /// </summary>
    internal static void Record(string path, object exceptionObject, bool isTerminating, DateTimeOffset at)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var entry =
                at.ToString("O", CultureInfo.InvariantCulture) +
                $" unhandled exception terminating={isTerminating}" + Environment.NewLine +
                exceptionObject + Environment.NewLine + Environment.NewLine;

            File.AppendAllText(path, entry);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
        }
    }
}
